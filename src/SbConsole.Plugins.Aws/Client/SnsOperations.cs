using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using CreateTopicRequest_ = Amazon.SimpleNotificationService.Model.CreateTopicRequest;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The only real implementation of ISnsOperations. Mirrors SqsOperations' construction pattern
/// exactly: a fresh client per call, built from the parsed secret, with ServiceURL/UseHttp
/// forwarded from the shared config so a custom LocalStack endpoint redirects SNS too.
/// </summary>
public sealed class SnsOperations : ISnsOperations
{
    private static AmazonSimpleNotificationServiceClient BuildSnsClient(string secret)
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
            var subCount = 0;
            var pendingCount = 0;
            var isKms = false;
            try
            {
                var attrs = await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }, ct);
                subCount = int.TryParse(attrs.Attributes.GetValueOrDefault("SubscriptionsConfirmed"), out var confirmed) ? confirmed : 0;
                pendingCount = int.TryParse(attrs.Attributes.GetValueOrDefault("SubscriptionsPending"), out var pending) ? pending : 0;
                isKms = attrs.Attributes.ContainsKey("KmsMasterKeyId");
            }
            catch (Exception) when (ct.IsCancellationRequested is false)
            {
                // Left at zero/false -- the caller renders this row's counts as unavailable rather
                // than blanking the whole page (design spec §2's partial-failure rule).
            }

            summaries.Add(new TopicSummary(name, topicArn, isFifo, subCount, pendingCount, isKms));
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
        return new SubscriptionSummary(subscriptionArn, protocol, endpoint, isPending, rawDelivery, filterPolicy);
    }

    // Placeholder throws for the remaining interface members -- implemented in Tasks 5, 6, 7.
    // These throws exist only so the class compiles as a complete ISnsOperations implementation;
    // nothing calls them until those tasks wire up their own handlers/pages.
    public Task<string> SubscribeAsync(string secret, SubscribeRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 5.");
    public Task UnsubscribeAsync(string secret, string subscriptionArn, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 5.");
    public Task PublishAsync(string secret, string topicArn, SnsPublishRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 6.");
    public Task<long> GetDeliveryFailureCountAsync(string secret, string topicName, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 7.");
}
