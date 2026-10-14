namespace SbConsole.Plugins.Redis.Client;

public sealed record HashFieldPage(IReadOnlyList<HashFieldEntry> Fields, long Cursor, bool Complete, long TotalFieldCount);

/// <summary>
/// <see cref="SizeBytes"/> is the UTF-8 byte length of <see cref="Value"/> computed client-side —
/// Redis has no per-hash-field MEMORY command (MEMORY USAGE only works on whole keys), unlike
/// KeySummary's ApproximateSizeBytes which comes from a real MEMORY USAGE call.
/// </summary>
public sealed record HashFieldEntry(string Field, string Value, long SizeBytes);
