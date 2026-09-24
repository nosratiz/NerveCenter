using System.Text.Json;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using CreateTopicRequest_ = Amazon.SimpleNotificationService.Model.CreateTopicRequest;
using SubscribeRequest_ = Amazon.SimpleNotificationService.Model.SubscribeRequest;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The only real implementation of ISnsOperations. Mirrors SqsOperations' construction pattern
/// exactly: a fresh client per call, built from the parsed secret, with ServiceURL/UseHttp
/// forwarded from the shared config so a custom LocalStack endpoint redirects SNS too.
/// </summary>
public sealed class SnsOperations : ISnsOperations
{
    internal static AmazonSimpleNotificationServiceClient BuildSnsClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var snsConfig = new AmazonSimpleNotificationServiceConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
            ServiceURL = sqsConfig.ServiceURL,
            UseHttp = sqsConfig.UseHttp,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonSimpleNotificationServiceClient(snsConfig) : new AmazonSimpleNotificationServiceClient(credentials, snsConfig);
    }

    private static AmazonCloudWatchClient BuildCloudWatchClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var cwConfig = new AmazonCloudWatchConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
            ServiceURL = sqsConfig.ServiceURL,
            UseHttp = sqsConfig.UseHttp,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonCloudWatchClient(cwConfig) : new AmazonCloudWatchClient(credentials, cwConfig);
    }

    internal static string TopicNameFromArn(string topicArn) => topicArn[(topicArn.LastIndexOf(':') + 1)..];

    public async Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string secret, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var topicArns = new List<string>();
        string? nextToken = null;
        do
        {
            var page = await sns.ListTopicsAsync(new ListTopicsRequest { NextToken = nextToken }, ct);
            topicArns.AddRange(page.Topics.Select(t => t.TopicArn));
            nextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        var summaries = new List<TopicSummary>();
        foreach (var topicArn in topicArns)
        {
            var name = TopicNameFromArn(topicArn);
            var isFifo = name.EndsWith(".fifo", StringComparison.Ordinal);
            try
            {
                var attrs = await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }, ct);
                var subCount = int.TryParse(attrs.Attributes.GetValueOrDefault("SubscriptionsConfirmed"), out var confirmed) ? confirmed : 0;
                var pendingCount = int.TryParse(attrs.Attributes.GetValueOrDefault("SubscriptionsPending"), out var pending) ? pending : 0;
                var isKms = attrs.Attributes.ContainsKey("KmsMasterKeyId");
                summaries.Add(new TopicSummary(name, topicArn, isFifo, subCount, pendingCount, isKms));
            }
            catch (Exception) when (ct.IsCancellationRequested is false)
            {
                // AttributesUnavailable: true -- the caller renders this row's counts as unavailable
                // (dashed) rather than a misleading real-looking zero (design spec §2's
                // partial-failure rule).
                summaries.Add(new TopicSummary(name, topicArn, isFifo, 0, 0, false, AttributesUnavailable: true));
            }
        }

        return summaries;
    }

    public async Task<string> CreateTopicAsync(string secret, CreateTopicRequest request, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var name = request.IsFifo && !request.Name.EndsWith(".fifo", StringComparison.Ordinal)
            ? $"{request.Name}.fifo"
            : request.Name;

        var attributes = new Dictionary<string, string>();
        if (request.IsFifo)
        {
            attributes["FifoTopic"] = "true";
            if (request.ContentBasedDeduplication is { } dedup)
            {
                attributes["ContentBasedDeduplication"] = dedup.ToString().ToLowerInvariant();
            }
        }

        if (request.KmsKeyId is { } kmsKeyId)
        {
            attributes["KmsMasterKeyId"] = kmsKeyId;
        }

        var response = await sns.CreateTopicAsync(new CreateTopicRequest_ { Name = name, Attributes = attributes }, ct);
        return response.TopicArn;
    }

    public async Task DeleteTopicAsync(string secret, string topicArn, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        await sns.DeleteTopicAsync(topicArn, ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetTopicAttributesAsync(string secret, string topicArn, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var response = await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }, ct);
        return new Dictionary<string, string>(response.Attributes ?? [], StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string secret, string topicArn, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var subscriptions = new List<Subscription>();
        string? nextToken = null;
        do
        {
            var page = await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = nextToken }, ct);
            subscriptions.AddRange(page.Subscriptions);
            nextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        var summaries = new List<SubscriptionSummary>();
        foreach (var sub in subscriptions)
        {
            var attributes = new Dictionary<string, string>();
            if (sub.SubscriptionArn != "PendingConfirmation")
            {
                try
                {
                    var attrsResponse = await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub.SubscriptionArn }, ct);
                    attributes = new Dictionary<string, string>(attrsResponse.Attributes);
                }
                catch (Exception) when (ct.IsCancellationRequested is false)
                {
                    // Left empty -- ClassifySubscription treats missing attributes as "unknown,
                    // not failing," same partial-failure rule as everywhere else in this plugin.
                }
            }

            summaries.Add(ClassifySubscription(sub.SubscriptionArn, sub.Protocol, sub.Endpoint, attributes));
        }

        return summaries;
    }

    // Pure static so it's unit-testable without a real AWS account -- mirrors SqsOperations.
    // ToQueueSummary/ExtractDeadLetterTargetArn's reasoning. Internal so SnsOperationsTests can
    // assert it directly (InternalsVisibleTo already covers the test project).
    internal static SubscriptionSummary ClassifySubscription(string subscriptionArn, string protocol, string endpoint, IReadOnlyDictionary<string, string> attributes)
    {
        var isPending = subscriptionArn == "PendingConfirmation";
        bool? rawDelivery = attributes.TryGetValue("RawMessageDelivery", out var raw) && bool.TryParse(raw, out var parsedRaw) ? parsedRaw : null;
        var filterPolicy = attributes.GetValueOrDefault("FilterPolicy");
        var filterPolicyScope = attributes.GetValueOrDefault("FilterPolicyScope");
        return new SubscriptionSummary(subscriptionArn, protocol, endpoint, isPending, rawDelivery, filterPolicy, FilterPolicyScope: filterPolicyScope);
    }

    // ListSubscriptions returns up to 100 per page, so 20 pages = the first 2,000 subscriptions in
    // the account. Past that the scan stops and says so rather than walking an unbounded account.
    internal const int MaxEndpointScanPages = 20;

    public async Task<EndpointSubscriptions> ListSubscriptionsForEndpointAsync(string secret, string endpoint, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        return await ScanSubscriptionsForEndpointAsync(
            async (nextToken, token) =>
            {
                var page = await sns.ListSubscriptionsAsync(new ListSubscriptionsRequest { NextToken = nextToken }, token);
                return (page.Subscriptions ?? [], page.NextToken);
            },
            endpoint, MaxEndpointScanPages, ct);
    }

    // The paging loop, with the page fetch injected so the cap is unit-testable without AWS.
    internal static async Task<EndpointSubscriptions> ScanSubscriptionsForEndpointAsync(
        Func<string?, CancellationToken, Task<(IReadOnlyList<Subscription> Page, string? NextToken)>> fetchPage,
        string endpoint, int maxPages, CancellationToken ct)
    {
        var subscriptions = new List<Subscription>();
        string? nextToken = null;
        var pages = 0;
        do
        {
            var (page, next) = await fetchPage(nextToken, ct);
            subscriptions.AddRange(page);
            nextToken = next;
            pages++;
        } while (!string.IsNullOrEmpty(nextToken) && pages < maxPages);

        return new EndpointSubscriptions(
            FilterSubscriptionsByEndpoint(subscriptions, endpoint),
            IsTruncated: !string.IsNullOrEmpty(nextToken),
            ScannedCount: subscriptions.Count);
    }

    // Pure static so the client-side endpoint filter is unit-testable without AWS. Ordinal match --
    // SQS/Lambda ARNs and URLs are case-sensitive identifiers.
    internal static IReadOnlyList<SubscriptionSummary> FilterSubscriptionsByEndpoint(IEnumerable<Subscription> subscriptions, string endpoint) =>
        subscriptions
            .Where(s => string.Equals(s.Endpoint, endpoint, StringComparison.Ordinal))
            .Select(s => ClassifySubscription(s.SubscriptionArn, s.Protocol, s.Endpoint, new Dictionary<string, string>()) with { TopicArn = s.TopicArn })
            .ToList();

    public async Task<string> SubscribeAsync(string secret, SubscribeRequest request, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var response = await sns.SubscribeAsync(new SubscribeRequest_
        {
            TopicArn = request.TopicArn,
            Protocol = request.Protocol,
            Endpoint = request.Endpoint,
            Attributes = BuildSubscribeAttributes(request),
        }, ct);
        return response.SubscriptionArn;
    }

    private const string FilterPolicyAttribute = "FilterPolicy";
    private const string FilterPolicyScopeAttribute = "FilterPolicyScope";
    private const string DefaultFilterPolicyScope = "MessageAttributes";

    // Pure static so the Subscribe attribute map is unit-testable without AWS. FilterPolicy/
    // FilterPolicyScope are only sent when a policy is actually given -- an empty FilterPolicy
    // attribute on Subscribe is pointless, and a scope without a policy is rejected by SNS.
    internal static Dictionary<string, string> BuildSubscribeAttributes(SubscribeRequest request)
    {
        var attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = request.RawMessageDelivery.ToString().ToLowerInvariant() };
        if (!string.IsNullOrWhiteSpace(request.FilterPolicy))
        {
            attributes[FilterPolicyAttribute] = request.FilterPolicy;
            attributes[FilterPolicyScopeAttribute] = string.IsNullOrWhiteSpace(request.FilterPolicyScope) ? DefaultFilterPolicyScope : request.FilterPolicyScope;
        }

        return attributes;
    }

    public async Task SetSubscriptionFilterPolicyAsync(string secret, string subscriptionArn, string? policyJson, string scope, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        string? currentPolicy = null;
        string? currentScope = null;
        if (!string.IsNullOrWhiteSpace(policyJson))
        {
            // Only needed to order the two SetSubscriptionAttributes calls (see
            // PlanFilterPolicyUpdates) -- a clear never looks at the current state.
            var current = await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subscriptionArn }, ct);
            currentPolicy = current.Attributes?.GetValueOrDefault(FilterPolicyAttribute);
            currentScope = current.Attributes?.GetValueOrDefault(FilterPolicyScopeAttribute);
        }

        await ApplyFilterPolicyUpdatesAsync(
            PlanFilterPolicyUpdates(currentPolicy, currentScope, policyJson, scope),
            (name, value, token) => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subscriptionArn,
                AttributeName = name,
                AttributeValue = value,
            }, token),
            ct);
    }

    // Makes the planned calls in order (the setter is injected so this is unit-testable without
    // AWS). A failure on the first call changed nothing and is rethrown as-is; a failure after an
    // earlier call succeeded left the subscription half-updated, which is reported explicitly as a
    // FilterPolicyPartiallyAppliedException naming what did and didn't land.
    internal static async Task ApplyFilterPolicyUpdatesAsync(
        IReadOnlyList<(string Name, string Value)> plan, Func<string, string, CancellationToken, Task> setAttribute, CancellationToken ct)
    {
        (string Name, string Value)? applied = null;
        foreach (var (name, value) in plan)
        {
            try
            {
                await setAttribute(name, value, ct);
            }
            catch (Exception ex) when (applied is { } done)
            {
                throw new FilterPolicyPartiallyAppliedException(DescribePartialUpdate(done, (name, value)), ex);
            }

            applied = (name, value);
        }
    }

    private static string DescribePartialUpdate((string Name, string Value) applied, (string Name, string Value) failed) =>
        applied.Name == FilterPolicyScopeAttribute
            ? $"The subscription was left partially updated: the scope was changed to {applied.Value}, but setting the new filter policy failed — the previous filter policy is now evaluated under the {applied.Value} scope."
            : $"The subscription was left partially updated: the new filter policy was set, but changing the scope to {failed.Value} failed — the new policy is evaluated under the previous scope.";

    // Pure static: which SetSubscriptionAttributes calls to make, in order. SetSubscriptionAttributes
    // sets one attribute per call and SNS validates the policy against the scope in force at that
    // moment, so the order matters when the scope changes:
    // - Clearing: a single FilterPolicy = "" call. Assumption (per AWS's SetSubscriptionAttributes
    //   docs, "an empty value removes the filter policy"): the installed AWSSDK.SimpleNotificationService
    //   3.7.400.62 marshals AttributeValue = "" onto the wire (IsSetAttributeValue is a null check,
    //   verified against the DLL). FilterPolicyScope is deliberately left alone -- a scope without a
    //   policy is meaningless and SNS rejects setting it on its own.
    // - Same scope: FilterPolicy only.
    // - To MessageBody with a policy already present: scope first, so a nested (body-only) new policy
    //   is validated under MessageBody. The existing policy must survive the scope switch -- any
    //   attribute-scope policy is flat, which is also valid under MessageBody.
    // - Otherwise (no current policy -- SNS won't take a scope without one -- or switching to
    //   MessageAttributes, where the new policy must be flat and so is also valid under the old body
    //   scope, while the old body policy may be nested): policy first, then scope.
    internal static IReadOnlyList<(string Name, string Value)> PlanFilterPolicyUpdates(
        string? currentPolicy, string? currentScope, string? newPolicy, string newScope)
    {
        if (string.IsNullOrWhiteSpace(newPolicy))
        {
            return [(FilterPolicyAttribute, "")];
        }

        var effectiveCurrentScope = string.IsNullOrEmpty(currentScope) ? DefaultFilterPolicyScope : currentScope;
        if (string.Equals(effectiveCurrentScope, newScope, StringComparison.Ordinal))
        {
            return [(FilterPolicyAttribute, newPolicy)];
        }

        return newScope == "MessageBody" && !string.IsNullOrWhiteSpace(currentPolicy)
            ? [(FilterPolicyScopeAttribute, newScope), (FilterPolicyAttribute, newPolicy)]
            : [(FilterPolicyAttribute, newPolicy), (FilterPolicyScopeAttribute, newScope)];
    }

    public async Task UnsubscribeAsync(string secret, string subscriptionArn, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        await sns.UnsubscribeAsync(subscriptionArn, ct);
    }

    public async Task PublishAsync(string secret, string topicArn, SnsPublishRequest request, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var sdkRequest = new Amazon.SimpleNotificationService.Model.PublishRequest { TopicArn = topicArn, Message = request.Message };
        if (request.Subject is { } subject)
        {
            sdkRequest.Subject = subject;
        }

        if (request.MessageAttributes is { Count: > 0 } attributes)
        {
            sdkRequest.MessageAttributes = attributes.ToDictionary(
                kv => kv.Key,
                kv => new Amazon.SimpleNotificationService.Model.MessageAttributeValue { DataType = "String", StringValue = kv.Value });
        }

        if (request.MessageGroupId is { } groupId)
        {
            sdkRequest.MessageGroupId = groupId;
        }

        if (request.MessageDeduplicationId is { } dedupId)
        {
            sdkRequest.MessageDeduplicationId = dedupId;
        }

        await sns.PublishAsync(sdkRequest, ct);
    }

    // Pure static, unit-testable without a real AWS account -- the fan-out preview's core logic.
    // SNS filter policies match on MessageAttributes: a policy is a JSON object of
    // attributeName -> array-of-allowed-values (the subset of SNS filter-policy syntax this plugin
    // supports for evaluation -- $or/anything-but/numeric-range operators are not evaluated and a
    // policy using them is treated as "no match," which is the conservative, safe direction to be
    // wrong in for a preview). A null/empty policy always matches (no filter = receives everything).
    // Malformed JSON is treated as no match, not an exception -- a preview must never crash the
    // Publish dialog over a policy it can't parse.
    internal static bool EvaluateFilterMatch(string? filterPolicyJson, IReadOnlyDictionary<string, string> messageAttributes)
    {
        if (string.IsNullOrEmpty(filterPolicyJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(filterPolicyJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!messageAttributes.TryGetValue(property.Name, out var value))
                {
                    return false;
                }

                var matchesThisKey = false;
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var allowed in property.Value.EnumerateArray())
                    {
                        if (allowed.ValueKind == JsonValueKind.String && allowed.GetString() == value)
                        {
                            matchesThisKey = true;
                            break;
                        }
                    }
                }

                if (!matchesThisKey)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<long> GetDeliveryFailureCountAsync(string secret, string topicName, CancellationToken ct = default)
    {
        using var cloudWatch = BuildCloudWatchClient(secret);
        var now = DateTime.UtcNow;
        var response = await cloudWatch.GetMetricStatisticsAsync(new GetMetricStatisticsRequest
        {
            Namespace = "AWS/SNS",
            MetricName = "NumberOfNotificationsFailed",
            Dimensions = [new Dimension { Name = "TopicName", Value = topicName }],
            StartTimeUtc = now.AddHours(-24),
            EndTimeUtc = now,
            Period = 86400,
            Statistics = ["Sum"],
        }, ct);

        // No datapoints means no failures were reported in the window, not an error -- CloudWatch
        // simply has nothing to return when a metric never fired.
        return response.Datapoints.Count == 0 ? 0 : (long)response.Datapoints.Sum(d => d.Sum);
    }
}
