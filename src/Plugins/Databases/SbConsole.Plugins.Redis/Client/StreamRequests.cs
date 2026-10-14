namespace SbConsole.Plugins.Redis.Client;

/// <summary>Mirrors ScanKeysRequest (design spec §5) but always implicitly TYPE-filtered to "stream" -- there's no separate type filter parameter because ScanStreamsAsync exists only to list streams.</summary>
public sealed record ScanStreamsRequest(int Database, string? Pattern, long Cursor, int Count);

/// <summary>Mirrors KeyScanPage exactly (design spec §5) -- same SCAN cursor/progress-bar shape the Keys browser already uses, reused rather than re-invented for streams.</summary>
public sealed record StreamScanPage(
    IReadOnlyList<StreamSummary> Matches,
    long Cursor,
    bool Complete,
    long KeysScannedSoFar,
    long DatabaseKeyCountAtScanStart);
