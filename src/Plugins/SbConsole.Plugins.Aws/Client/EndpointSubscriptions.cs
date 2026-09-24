namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The SNS subscriptions delivering to one endpoint (a queue ARN), from a client-side scan of the
/// account's ListSubscriptions pages. SNS has no server-side endpoint filter, so the scan is capped
/// (SnsOperations.MaxEndpointScanPages): IsTruncated means more pages existed and only the first
/// ScannedCount subscriptions were examined -- the list may be incomplete.
/// </summary>
public sealed record EndpointSubscriptions(IReadOnlyList<SubscriptionSummary> Subscriptions, bool IsTruncated, int ScannedCount);
