namespace SbConsole.Plugins.Aws.Client;

public sealed record SubscriptionSummary(
    string SubscriptionArn, string Protocol, string Endpoint, bool IsPending,
    bool? RawMessageDelivery, string? FilterPolicyJson,
    // The topic this subscription belongs to. Set by ListSubscriptionsForEndpointAsync (the queue
    // detail page's "subscribed to N topics" panel needs it); left null by the per-topic
    // ListSubscriptionsAsync, whose caller already knows the topic.
    string? TopicArn = null)
{
    public string? TopicName => TopicArn is null ? null : SnsOperations.TopicNameFromArn(TopicArn);
}
