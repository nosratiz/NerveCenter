namespace SbConsole.Plugins.Redis.Client;

/// <summary>One row in a stream's consumer-groups table (design spec §3a), from XINFO GROUPS. Lag is null on servers too old to report it (Redis &lt; 7.0), matching StreamGroupInfo.Lag's own nullability.</summary>
public sealed record StreamConsumerGroupSummary(
    string Name,
    int ConsumerCount,
    long PendingCount,
    string LastDeliveredId,
    long? Lag);
