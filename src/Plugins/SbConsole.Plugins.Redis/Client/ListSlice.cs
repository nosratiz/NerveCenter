namespace SbConsole.Plugins.Redis.Client;

public sealed record ListSlice(long Length, IReadOnlyList<ListEntry> Head, IReadOnlyList<ListEntry> Tail);

/// <summary>Index uses Redis's own negative-from-tail convention for Tail entries (LRANGE -N..-1).</summary>
public sealed record ListEntry(long Index, string Value);
