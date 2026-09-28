namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// Single-stream detail (design spec §3a), from XINFO STREAM. Deliberately has no total
/// consumer-count field -- XINFO STREAM doesn't report one (only ConsumerGroupCount), and summing
/// per-group counts would mean an extra XINFO GROUPS round trip an overview call doesn't otherwise
/// need; the per-group breakdown is already available via StreamConsumerGroupSummary.ConsumerCount
/// for the page's separate consumer-groups table.
/// </summary>
public sealed record StreamOverview(
    string Key,
    long Length,
    int RadixTreeKeys,
    int RadixTreeNodes,
    int ConsumerGroupCount,
    string LastGeneratedId,
    string? FirstEntryId,
    string? LastEntryId);
