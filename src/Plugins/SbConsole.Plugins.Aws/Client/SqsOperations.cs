using System.Text.Json;
using Amazon.SecurityToken;
using Amazon.SimpleNotificationService.Model;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The only real implementation of ISqsOperations. Its own methods get light test coverage by
/// necessity -- they can't be meaningfully unit-tested without a real or emulated AWS account
/// (design spec §8) -- the substitutable interface is where the test leverage is.
/// </summary>
public sealed class SqsOperations : ISqsOperations
{
    private static AmazonSQSClient BuildSqsClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var config = AwsCredentialsFactory.BuildConfig(parsed);
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonSQSClient(config) : new AmazonSQSClient(credentials, config);
    }

    private static AmazonSecurityTokenServiceClient BuildStsClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var stsConfig = new AmazonSecurityTokenServiceConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
            // LocalStack emulates STS too, so a custom endpoint (design spec §2) must redirect the
            // GetCallerIdentity pre-check the same way it redirects SQS calls -- otherwise a
            // connection test against an emulator falls through to real AWS STS and fails there
            // instead of succeeding against the emulator.
            ServiceURL = sqsConfig.ServiceURL,
            UseHttp = sqsConfig.UseHttp,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonSecurityTokenServiceClient(stsConfig) : new AmazonSecurityTokenServiceClient(credentials, stsConfig);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        string identity;
        try
        {
            using var sts = BuildStsClient(secret);
            var response = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
            identity = response.Account;
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyAwsError.From(ex));
        }

        // Credentials are valid (GetCallerIdentity succeeded). A denied ListQueues/ListTopics probe
        // is reported as a Failed check, not a failed connection test -- design spec §3's "under-
        // permissioned" outcome: Success stays true, nothing is persisted, no button anywhere is
        // hidden as a result (design spec §8, Out of scope).
        var queuesCheck = await ConnectionProbe.RunAsync("Queues visible", async token =>
        {
            using var sqs = BuildSqsClient(secret);
            var response = await sqs.ListQueuesAsync(new ListQueuesRequest { MaxResults = 1 }, token);
            return response.QueueUrls.Count.ToString();
        }, ct);
        var topicsCheck = await ConnectionProbe.RunAsync("Topics visible", async token =>
        {
            // ListTopics has no MaxResults; one page (up to 100 ARNs) is still a single cheap call.
            using var sns = SnsOperations.BuildSnsClient(secret);
            var response = await sns.ListTopicsAsync(new ListTopicsRequest(), token);
            return response.Topics.Count.ToString();
        }, ct);

        return new ConnectionTestResult(true, Identity: identity, Checks: [queuesCheck, topicsCheck]);
    }

    public async Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? namePrefix, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var listRequest = new ListQueuesRequest { QueueNamePrefix = namePrefix };
        var queueUrls = new List<string>();
        string? nextToken = null;
        do
        {
            listRequest.NextToken = nextToken;
            var page = await sqs.ListQueuesAsync(listRequest, ct);
            queueUrls.AddRange(page.QueueUrls);
            nextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        // First pass: fetch every queue's attributes in one call each (AttributeNames=[All]) and
        // cache them, so the target-ARN counting pass below doesn't need a second round-trip per
        // queue -- ListQueuesAsync already fetches this data, DLQ-ness is purely a client-side
        // computation over it.
        //
        // Sequential, deliberately -- fan-out here is a separate (optional) performance concern.
        // Each call is wrapped individually: a single queue's GetQueueAttributes failing (e.g. mid-
        // load throttling) must leave that one row degraded, not blank the whole page (design spec
        // §5's partial-failure resilience requirement).
        var attributesByUrl = new Dictionary<string, IDictionary<string, string>>();
        foreach (var queueUrl in queueUrls)
        {
            try
            {
                var attributesResponse = await sqs.GetQueueAttributesAsync(
                    new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["All"] }, ct);
                attributesByUrl[queueUrl] = attributesResponse.Attributes;
            }
            catch (Exception) when (ct.IsCancellationRequested is false)
            {
                // Left out of attributesByUrl -- the final loop below turns a missing entry into a
                // QueueSummary.Unavailable row instead of losing the whole page.
            }
        }

        // Count, per target ARN, how many queues' RedrivePolicy points at it -- that count is what
        // makes a queue an actual dead-letter target (AWS's StartMessageMoveTask requires SourceArn
        // to be a queue that other queues redrive into), not merely having a RedrivePolicy of its
        // own (that just means it dead-letters TO somewhere else).
        var deadLetterSourceCounts = new Dictionary<string, int>();
        foreach (var attributes in attributesByUrl.Values)
        {
            var targetArn = attributes.TryGetValue("RedrivePolicy", out var redrivePolicy)
                ? ExtractDeadLetterTargetArn(redrivePolicy)
                : null;
            if (targetArn is not null)
            {
                deadLetterSourceCounts[targetArn] = deadLetterSourceCounts.GetValueOrDefault(targetArn) + 1;
            }
        }

        var summaries = new List<QueueSummary>();
        foreach (var queueUrl in queueUrls)
        {
            var name = QueueNameFromUrl(queueUrl);
            if (!attributesByUrl.TryGetValue(queueUrl, out var attributes))
            {
                summaries.Add(QueueSummary.Unavailable(name, queueUrl));
                continue;
            }

            var queueArn = attributes.TryGetValue("QueueArn", out var arn) ? arn : "";
            var deadLetterSourceCount = deadLetterSourceCounts.GetValueOrDefault(queueArn);
            summaries.Add(ToQueueSummary(name, queueUrl, attributes, deadLetterSourceCount));
        }

        return summaries;
    }

    // Extracted as a pure static function so the attribute-dictionary-to-QueueSummary mapping is
    // unit-testable without a real AWS account, same reasoning as
    // ConfluentKafkaOperations.Decode/IsEndOfPartition. Internal (not private) so
    // SqsOperationsTests can assert it directly -- InternalsVisibleTo already covers the test
    // project (Task 1's csproj).
    internal static QueueSummary ToQueueSummary(string name, string queueUrl, IDictionary<string, string> attributes, int deadLetterSourceCount)
    {
        long GetLong(string key) => attributes.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) ? parsed : 0;
        bool GetBool(string key) => attributes.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

        return new QueueSummary(
            Name: name,
            QueueUrl: queueUrl,
            QueueArn: attributes.TryGetValue("QueueArn", out var queueArn) ? queueArn : "",
            IsFifo: GetBool("FifoQueue"),
            ApproxVisible: GetLong("ApproximateNumberOfMessages"),
            ApproxInFlight: GetLong("ApproximateNumberOfMessagesNotVisible"),
            ApproxDelayed: GetLong("ApproximateNumberOfMessagesDelayed"),
            HasDeadLetterTarget: attributes.ContainsKey("RedrivePolicy"),
            IsKmsEncrypted: attributes.ContainsKey("KmsMasterKeyId"),
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(GetLong("CreatedTimestamp")),
            DeadLetterSourceCount: deadLetterSourceCount);
    }

    internal static string QueueNameFromUrl(string queueUrl) => queueUrl[(queueUrl.LastIndexOf('/') + 1)..];

    // Pure static so it's unit-testable without a real AWS account. RedrivePolicy is a JSON string
    // attribute (not a nested SQS structure) shaped {"deadLetterTargetArn":"arn:...",
    // "maxReceiveCount":N} -- never throws on null/empty/malformed input, since a queue with no
    // redrive policy, or one AWS shapes differently than expected, should just look like "no DLQ
    // target here" rather than blow up queue listing.
    internal static string? ExtractDeadLetterTargetArn(string? redrivePolicyJson)
    {
        if (string.IsNullOrEmpty(redrivePolicyJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(redrivePolicyJson);
            return document.RootElement.TryGetProperty("deadLetterTargetArn", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<QueueDetails> GetQueueDetailAsync(string secret, string queueUrl, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var attributesResponse = await sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["All"] }, ct);

        // Tags and dead-letter sources are secondary panels behind their own IAM permissions
        // (sqs:ListQueueTags, sqs:ListDeadLetterSourceQueues) -- a denial there degrades that one
        // field to null instead of failing the whole page (same partial-failure rule as
        // ListQueuesAsync's per-queue attribute calls).
        Dictionary<string, string>? tags;
        try
        {
            var tagsResponse = await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = queueUrl }, ct);
            tags = tagsResponse.Tags ?? new Dictionary<string, string>();
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            tags = null;
        }

        List<string>? sources = [];
        try
        {
            string? nextToken = null;
            do
            {
                var page = await sqs.ListDeadLetterSourceQueuesAsync(
                    new ListDeadLetterSourceQueuesRequest { QueueUrl = queueUrl, NextToken = nextToken }, ct);
                sources.AddRange(page.QueueUrls ?? []);
                nextToken = page.NextToken;
            } while (!string.IsNullOrEmpty(nextToken));
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            sources = null;
        }

        return ToQueueDetail(queueUrl, attributesResponse.Attributes ?? new Dictionary<string, string>(), tags, sources);
    }

    // Pure static, same reasoning as ToQueueSummary -- unit-testable without AWS.
    internal static QueueDetails ToQueueDetail(
        string queueUrl,
        IDictionary<string, string> attributes,
        IReadOnlyDictionary<string, string>? tags,
        IReadOnlyList<string>? deadLetterSourceQueueUrls)
    {
        long GetLong(string key) => attributes.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) ? parsed : 0;
        bool GetBool(string key) => attributes.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;
        DateTimeOffset? GetTimestamp(string key) =>
            attributes.TryGetValue(key, out var value) && long.TryParse(value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

        return new QueueDetails(
            Name: QueueNameFromUrl(queueUrl),
            QueueUrl: queueUrl,
            QueueArn: attributes.TryGetValue("QueueArn", out var queueArn) ? queueArn : "",
            IsFifo: GetBool("FifoQueue"),
            IsKmsEncrypted: attributes.ContainsKey("KmsMasterKeyId"),
            ApproxVisible: GetLong("ApproximateNumberOfMessages"),
            ApproxInFlight: GetLong("ApproximateNumberOfMessagesNotVisible"),
            ApproxDelayed: GetLong("ApproximateNumberOfMessagesDelayed"),
            CreatedAt: GetTimestamp("CreatedTimestamp"),
            LastModifiedAt: GetTimestamp("LastModifiedTimestamp"),
            Attributes: new Dictionary<string, string>(attributes),
            Tags: tags is null ? null : new Dictionary<string, string>(tags),
            RedrivePolicy: ParseRedrivePolicy(attributes.TryGetValue("RedrivePolicy", out var redrivePolicy) ? redrivePolicy : null),
            DeadLetterSourceQueueUrls: deadLetterSourceQueueUrls);
    }

    // Like ExtractDeadLetterTargetArn, but also reads maxReceiveCount -- accepted as a JSON number or
    // a numeric string, since AWS has returned both shapes. Never throws: a missing/malformed policy
    // is "no DLQ configured", and a policy with a target but an unreadable count keeps the target.
    internal static QueueRedrivePolicy? ParseRedrivePolicy(string? redrivePolicyJson)
    {
        if (string.IsNullOrEmpty(redrivePolicyJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(redrivePolicyJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("deadLetterTargetArn", out var target)
                || target.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(target.GetString()))
            {
                return null;
            }

            int? maxReceiveCount = null;
            if (root.TryGetProperty("maxReceiveCount", out var count))
            {
                if (count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var number))
                {
                    maxReceiveCount = number;
                }
                else if (count.ValueKind == JsonValueKind.String && int.TryParse(count.GetString(), out var parsed))
                {
                    maxReceiveCount = parsed;
                }
            }

            return new QueueRedrivePolicy(target.GetString()!, maxReceiveCount);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string> CreateQueueAsync(string secret, CreateQueueRequest request, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var name = request.IsFifo && !request.Name.EndsWith(".fifo", StringComparison.Ordinal)
            ? $"{request.Name}.fifo"
            : request.Name;

        var attributes = new Dictionary<string, string>
        {
            ["VisibilityTimeout"] = request.VisibilityTimeoutSeconds.ToString(),
            ["MessageRetentionPeriod"] = request.RetentionPeriodSeconds.ToString(),
            ["DelaySeconds"] = request.DelaySeconds.ToString(),
            ["MaximumMessageSize"] = request.MaxMessageSizeBytes.ToString(),
            ["ReceiveMessageWaitTimeSeconds"] = request.ReceiveWaitTimeSeconds.ToString(),
        };

        if (request.IsFifo)
        {
            attributes["FifoQueue"] = "true";
            if (request.ContentBasedDeduplication is { } dedup)
            {
                attributes["ContentBasedDeduplication"] = dedup.ToString().ToLowerInvariant();
            }

            if (request.HighThroughputFifo is true)
            {
                attributes["DeduplicationScope"] = "messageGroup";
                attributes["FifoThroughputLimit"] = "perMessageGroupId";
            }
        }

        if (request.DeadLetterTargetArn is { } dlqArn && request.MaxReceiveCount is { } maxReceives)
        {
            // Serialized via JsonSerializer, not raw string interpolation -- a quote or backslash
            // in a user-typed ARN would otherwise produce malformed JSON.
            attributes["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = dlqArn, maxReceiveCount = maxReceives });
        }

        if (request.KmsKeyId is { } kmsKeyId)
        {
            attributes["KmsMasterKeyId"] = kmsKeyId;
        }

        var response = await sqs.CreateQueueAsync(new Amazon.SQS.Model.CreateQueueRequest { QueueName = name, Attributes = attributes }, ct);
        return response.QueueUrl;
    }

    public async Task DeleteQueueAsync(string secret, string queueUrl, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.DeleteQueueAsync(queueUrl, ct);
    }

    public async Task PurgeQueueAsync(string secret, string queueUrl, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.PurgeQueueAsync(queueUrl, ct);
    }

    public async Task<IReadOnlyList<ReceivedMessage>> ReceiveMessagesAsync(
        string secret, string queueUrl, int maxMessages, int? visibilityTimeoutSeconds, int waitTimeSeconds, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var request = new Amazon.SQS.Model.ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = maxMessages,
            WaitTimeSeconds = waitTimeSeconds,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = [Amazon.SQS.MessageSystemAttributeName.All],
        };
        if (visibilityTimeoutSeconds is { } timeout)
        {
            request.VisibilityTimeout = timeout;
        }

        var response = await sqs.ReceiveMessageAsync(request, ct);
        return response.Messages.Select(ToReceivedMessage).ToList();
    }

    // Extracted as a pure static function, same reasoning as ToQueueSummary above -- unit-testable
    // without a real AWS account. Internal so SqsOperationsTests can assert it directly.
    internal static ReceivedMessage ToReceivedMessage(Amazon.SQS.Model.Message message)
    {
        var attributes = message.Attributes;
        var receiveCount = attributes.TryGetValue("ApproximateReceiveCount", out var countText) && int.TryParse(countText, out var count) ? count : 0;
        var sentTimestamp = attributes.TryGetValue("SentTimestamp", out var sentText) && long.TryParse(sentText, out var sentMillis)
            ? DateTimeOffset.FromUnixTimeMilliseconds(sentMillis)
            : DateTimeOffset.MinValue;
        var senderId = attributes.GetValueOrDefault("SenderId", "");
        var messageAttributes = message.MessageAttributes.ToDictionary(kv => kv.Key, kv => kv.Value.StringValue ?? "");
        // Returned because ReceiveMessagesAsync requests MessageSystemAttributeName.All.
        var groupId = attributes.TryGetValue("MessageGroupId", out var group) ? group : null;

        return new ReceivedMessage(
            message.MessageId, message.ReceiptHandle, message.Body, receiveCount,
            sentTimestamp, senderId, message.MD5OfBody, messageAttributes, groupId);
    }

    public async Task DeleteMessageAsync(string secret, string queueUrl, string receiptHandle, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.DeleteMessageAsync(queueUrl, receiptHandle, ct);
    }

    public async Task ChangeMessageVisibilityAsync(string secret, string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.ChangeMessageVisibilityAsync(queueUrl, receiptHandle, visibilityTimeoutSeconds, ct);
    }

    public async Task SendMessageAsync(string secret, string queueUrl, SendMessageRequest request, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var sqsRequest = new Amazon.SQS.Model.SendMessageRequest { QueueUrl = queueUrl, MessageBody = request.Body };
        if (request.DelaySeconds is { } delay)
        {
            sqsRequest.DelaySeconds = delay;
        }

        if (request.MessageAttributes is { Count: > 0 } attributes)
        {
            sqsRequest.MessageAttributes = attributes.ToDictionary(
                kv => kv.Key,
                kv => new Amazon.SQS.Model.MessageAttributeValue { DataType = "String", StringValue = kv.Value });
        }

        if (request.MessageGroupId is { } groupId)
        {
            sqsRequest.MessageGroupId = groupId;
        }

        if (request.MessageDeduplicationId is { } dedupId)
        {
            sqsRequest.MessageDeduplicationId = dedupId;
        }

        await sqs.SendMessageAsync(sqsRequest, ct);
    }

    public async Task<string> StartRedriveTaskAsync(string secret, string sourceQueueArn, string destinationQueueArn, int? maxMessagesPerSecond, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var request = new Amazon.SQS.Model.StartMessageMoveTaskRequest
        {
            SourceArn = sourceQueueArn,
            DestinationArn = destinationQueueArn,
        };
        if (maxMessagesPerSecond is { } rate)
        {
            request.MaxNumberOfMessagesPerSecond = rate;
        }

        var response = await sqs.StartMessageMoveTaskAsync(request, ct);
        return response.TaskHandle;
    }

    // MaxResults is AWS's documented maximum (default 1) -- enough history to show the task that
    // just finished next to one that's still running.
    private const int MaxMoveTasks = 10;

    public async Task<IReadOnlyList<MessageMoveTaskSummary>> ListMessageMoveTasksAsync(string secret, string sourceQueueArn, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var response = await sqs.ListMessageMoveTasksAsync(
            new Amazon.SQS.Model.ListMessageMoveTasksRequest { SourceArn = sourceQueueArn, MaxResults = MaxMoveTasks }, ct);
        return (response.Results ?? []).Select(ToMessageMoveTaskSummary).ToList();
    }

    public async Task CancelMessageMoveTaskAsync(string secret, string taskHandle, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.CancelMessageMoveTaskAsync(new Amazon.SQS.Model.CancelMessageMoveTaskRequest { TaskHandle = taskHandle }, ct);
    }

    // Pure mapping, unit-testable without AWS (same as ToReceivedMessage). AWSSDK.SQS 3.7.400 models
    // the counts and StartedTimestamp as non-nullable longs, so "not reported" arrives as 0:
    // StartedTimestamp is epoch *milliseconds*, and a 0 ToMove/timestamp means unknown, not zero.
    internal static MessageMoveTaskSummary ToMessageMoveTaskSummary(Amazon.SQS.Model.ListMessageMoveTasksResultEntry entry) => new(
        TaskHandle: string.IsNullOrEmpty(entry.TaskHandle) ? null : entry.TaskHandle,
        Status: entry.Status ?? "",
        SourceArn: entry.SourceArn ?? "",
        DestinationArn: string.IsNullOrEmpty(entry.DestinationArn) ? null : entry.DestinationArn,
        MessagesMoved: entry.ApproximateNumberOfMessagesMoved,
        MessagesToMove: entry.ApproximateNumberOfMessagesToMove > 0 ? entry.ApproximateNumberOfMessagesToMove : null,
        FailureReason: string.IsNullOrEmpty(entry.FailureReason) ? null : entry.FailureReason,
        StartedAt: entry.StartedTimestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(entry.StartedTimestamp) : null);
}
