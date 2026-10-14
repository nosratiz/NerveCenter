namespace SbConsole.Plugins.Redis.Client;

public sealed record KeySummary(string Key, RedisKeyType Type, TimeSpan? Ttl, long ApproximateSizeBytes);

/// <summary>
/// One page of a SCAN loop. <see cref="Cursor"/> is the raw next-cursor SCAN returned — 0 means
/// the whole keyspace has been visited (<see cref="Complete"/>). <see cref="KeysScannedSoFar"/> is
/// this step's own COUNT hint (Redis's SCAN COUNT is "the amount of work to do per call", not an
/// exact count of elements examined) — the caller (Keys.razor) accumulates it across pages for the
/// progress bar, since a stateless per-call RedisOperations has no memory of earlier pages itself.
/// </summary>
public sealed record KeyScanPage(
    IReadOnlyList<KeySummary> Matches, long Cursor, bool Complete,
    long KeysScannedSoFar, long DatabaseKeyCountAtScanStart);
