namespace SbConsole.Plugins.Redis.Client;

/// <summary>One row in a stream's consumer-groups table (design spec §3a), from XINFO GROUPS. Lag is null on servers too old to report it (Redis &lt; 7.0), matching StreamGroupInfo.Lag's own nullability; LastDeliveredId is nullable to match StreamGroupInfo.LastDeliveredId's own binding, though Redis itself always reports "0-0" rather than a true null.</summary>
public sealed record StreamConsumerGroupSummary(
    string Name,
    int ConsumerCount,
    long PendingCount,
    string? LastDeliveredId,
    long? Lag);
