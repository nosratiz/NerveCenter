namespace SbConsole.Plugins.Redis.Client;

internal static class RedisMemoryByPrefixSampler
{
    public static IReadOnlyList<MemoryByPrefixEntry> Summarize(
        IReadOnlyList<SampledKey> samples, char delimiter, int topN)
    {
        var groups = samples
            .GroupBy(s => PrefixOf(s.Key, delimiter))
            .Select(g => new MemoryByPrefixEntry(
                g.Key,
                g.Sum(s => s.Bytes),
                g.Count(),
                g.Count(s => !s.HasTtl)))
            .OrderByDescending(e => e.SampledBytes)
            .ToList();

        if (groups.Count <= topN)
        {
            return groups;
        }

        var top = groups.Take(topN).ToList();
        var rest = groups.Skip(topN).ToList();
        top.Add(new MemoryByPrefixEntry(
            "other",
            rest.Sum(e => e.SampledBytes),
            rest.Sum(e => e.SampledKeyCount),
            rest.Sum(e => e.KeysWithoutTtlCount)));
        return top;
    }

    private static string PrefixOf(string key, char delimiter)
    {
        var index = key.IndexOf(delimiter);
        return index < 0 ? key : key[..(index + 1)];
    }
}
