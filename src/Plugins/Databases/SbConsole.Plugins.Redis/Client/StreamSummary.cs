namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// One row in the Streams page's stream list (design spec §3a). ApproximateSizeBytes is
/// deliberately not included -- unlike a plain key, MEMORY USAGE on a stream key walks the whole
/// radix tree server-side and is far more expensive than the rest of a SCAN TYPE stream page, so
/// it isn't fetched per-row here (matching the design brief's "~memory if cheaply available" --
/// it isn't cheap for streams, so it's left out; RadixTreeKeys/RadixTreeNodes in StreamOverview
/// give the closer, single-stream-detail-only substitute XINFO STREAM already returns for free).
/// </summary>
public sealed record StreamSummary(
    string Key,
    long Length,
    string LastGeneratedId,
    string? FirstEntryId,
    string? LastEntryId);
