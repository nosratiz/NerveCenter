namespace SbConsole.Plugins.Aws.Client;

public sealed record SubscriptionSummary(
    string SubscriptionArn, string Protocol, string Endpoint, bool IsPending,
    bool? RawMessageDelivery, string? FilterPolicyJson);
