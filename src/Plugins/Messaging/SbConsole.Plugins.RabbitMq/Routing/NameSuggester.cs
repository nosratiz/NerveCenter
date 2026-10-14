namespace SbConsole.Plugins.RabbitMq.Routing;

/// <summary>"Did you mean {closest}?" for a filter that matched nothing (1d empty-filter state).</summary>
public static class NameSuggester
{
    /// <summary>
    /// The candidate nearest to <paramref name="input"/>, compared case-insensitively. Only
    /// plausible candidates qualify -- a shared prefix of at least 3 characters, or an edit
    /// distance of at most max(3, input length / 2) -- and among those the longest shared prefix
    /// wins, then the smallest edit distance, then ordinal name order. Null when none qualifies.
    /// </summary>
    public static string? Closest(string input, IEnumerable<string> candidates)
    {
        var needle = input.Trim().ToLowerInvariant();
        if (needle.Length == 0) return null;

        var maxDistance = Math.Max(3, needle.Length / 2);

        return candidates
            .Select(name =>
            {
                var lowered = name.ToLowerInvariant();
                return (Name: name, Prefix: SharedPrefix(needle, lowered), Distance: Levenshtein.Distance(needle, lowered));
            })
            .Where(c => c.Prefix >= 3 || c.Distance <= maxDistance)
            .OrderByDescending(c => c.Prefix)
            .ThenBy(c => c.Distance)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name)
            .FirstOrDefault();
    }

    private static int SharedPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }
}
