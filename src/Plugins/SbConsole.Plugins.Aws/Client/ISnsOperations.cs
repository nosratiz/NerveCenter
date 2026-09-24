namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The seam between the plugin's handlers and the real AWSSDK.SimpleNotificationService/
/// AWSSDK.CloudWatch clients. Every method takes the connection secret as a parameter -- mirrors
/// SbConsole.Plugins.Aws.Client.ISqsOperations exactly.
/// </summary>
public interface ISnsOperations
{
    Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string secret, CancellationToken ct = default);
    Task<string> CreateTopicAsync(string secret, CreateTopicRequest request, CancellationToken ct = default);
    /// <summary>Destructive.</summary>
    Task DeleteTopicAsync(string secret, string topicArn, CancellationToken ct = default);

    /// <summary>Every attribute GetTopicAttributes returns for the topic, raw (Policy/DeliveryPolicy are JSON strings).</summary>
    Task<IReadOnlyDictionary<string, string>> GetTopicAttributesAsync(string secret, string topicArn, CancellationToken ct = default);

    Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string secret, string topicArn, CancellationToken ct = default);
    /// <summary>
    /// Every subscription (any topic) whose Endpoint equals <paramref name="endpoint"/> -- e.g. a
    /// queue ARN. SNS has no server-side filter for this, so it pages through ListSubscriptions and
    /// filters client-side, stopping after SnsOperations.MaxEndpointScanPages pages (reported via
    /// EndpointSubscriptions.IsTruncated). Subscription attributes are not fetched
    /// (RawMessageDelivery/FilterPolicyJson stay null).
    /// </summary>
    Task<EndpointSubscriptions> ListSubscriptionsForEndpointAsync(string secret, string endpoint, CancellationToken ct = default);
    Task<string> SubscribeAsync(string secret, SubscribeRequest request, CancellationToken ct = default);
    /// <summary>Mutating, not Destructive -- reversible by subscribing again.</summary>
    Task UnsubscribeAsync(string secret, string subscriptionArn, CancellationToken ct = default);

    /// <summary>
    /// Mutating. Sets (or, with a null/empty <paramref name="policyJson"/>, clears) the
    /// subscription's FilterPolicy via SetSubscriptionAttributes, with <paramref name="scope"/>
    /// ("MessageAttributes" | "MessageBody") as its FilterPolicyScope. Clearing never touches the
    /// scope. Only valid on a confirmed subscription -- a pending one has no real ARN.
    /// </summary>
    Task SetSubscriptionFilterPolicyAsync(string secret, string subscriptionArn, string? policyJson, string scope, CancellationToken ct = default);

    Task PublishAsync(string secret, string topicArn, SnsPublishRequest request, CancellationToken ct = default);

    /// <summary>Sum of NumberOfNotificationsFailed over the trailing 24h, via CloudWatch GetMetricStatistics. Zero datapoints means zero failures, not an error.</summary>
    Task<long> GetDeliveryFailureCountAsync(string secret, string topicName, CancellationToken ct = default);
}
