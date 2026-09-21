using Amazon.SecurityToken;
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
        try
        {
            using var sts = BuildStsClient(secret);
            await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyAwsError.From(ex));
        }

        // Credentials are valid (GetCallerIdentity succeeded). A denied ListQueues probe is
        // reported as a non-fatal note, not a failure -- design spec §4's "text-only diagnostics"
        // decision: nothing is persisted and no button is hidden as a result.
        try
        {
            using var sqs = BuildSqsClient(secret);
            await sqs.ListQueuesAsync(new ListQueuesRequest { MaxResults = 1 }, ct);
            return new ConnectionTestResult(true);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(true, $"Valid credentials; sqs:ListQueues denied — queue actions may fail ({FriendlyAwsError.From(ex)})");
        }
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

        var summaries = new List<QueueSummary>();
        foreach (var queueUrl in queueUrls)
        {
            var attributesResponse = await sqs.GetQueueAttributesAsync(
                new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["All"] }, ct);
            summaries.Add(ToQueueSummary(QueueNameFromUrl(queueUrl), queueUrl, attributesResponse.Attributes));
        }

        return summaries;
    }

    // Extracted as a pure static function so the attribute-dictionary-to-QueueSummary mapping is
    // unit-testable without a real AWS account, same reasoning as
    // ConfluentKafkaOperations.Decode/IsEndOfPartition. Internal (not private) so
    // SqsOperationsTests can assert it directly -- InternalsVisibleTo already covers the test
    // project (Task 1's csproj).
    internal static QueueSummary ToQueueSummary(string name, string queueUrl, IDictionary<string, string> attributes)
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
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(GetLong("CreatedTimestamp")));
    }

    internal static string QueueNameFromUrl(string queueUrl) => queueUrl[(queueUrl.LastIndexOf('/') + 1)..];

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
            attributes["RedrivePolicy"] = $"{{\"deadLetterTargetArn\":\"{dlqArn}\",\"maxReceiveCount\":{maxReceives}}}";
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

        return new ReceivedMessage(
            message.MessageId, message.ReceiptHandle, message.Body, receiveCount,
            sentTimestamp, senderId, message.MD5OfBody, messageAttributes);
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
}
