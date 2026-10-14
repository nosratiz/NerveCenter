namespace SbConsole.Plugins.Redis.Client;

public sealed record SortedSetSlice(long Cardinality, IReadOnlyList<SortedSetEntry> Entries, long RangeStart, long RangeStop);

public sealed record SortedSetEntry(string Member, double Score);
