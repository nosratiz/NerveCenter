using SbConsole.Sdk;

namespace SbConsole.Web.Wallboard;

/// <summary>
/// Turns already-retained per-resource metric history (SbConsole.Sdk.MetricHistoryStore) into
/// wallboard chart data. Pure and synchronous on purpose -- this is the test leverage point for
/// the wallboard's charts, the same role DashboardProblems plays for the Dashboard's problem list.
/// </summary>
public static class WallboardAggregator
{
    public sealed record Bucket(DateTimeOffset At, long TotalActive, long TotalDeadLetter);

    /// <summary>
    /// Produces one bucket per bucketSize step from (now - window) to now inclusive, each summing
    /// every resource's most-recent point at or before that bucket's end time (step-function
    /// interpolation -- correct for periodically-sampled point-in-time counts). A resource with no
    /// qualifying point yet (e.g. added after the window started) contributes zero to that bucket.
    /// </summary>
    public static IReadOnlyList<Bucket> BucketAndSum(
        IReadOnlyList<IReadOnlyList<MetricSnapshotPoint>> perResourceHistories,
        TimeSpan bucketSize, DateTimeOffset now, TimeSpan window)
    {
        var bucketCount = (int)(window / bucketSize);
        var buckets = new List<Bucket>(bucketCount);

        // MetricHistoryStore guarantees each history is stored oldest-first, and this loop visits
        // buckets in ascending chronological order (bucketEnd increases as i decreases) -- so each
        // resource's history can be walked once with a forward cursor instead of being re-filtered
        // and re-sorted from scratch for every bucket.
        var cursors = new int[perResourceHistories.Count];
        var latestPerResource = new MetricSnapshotPoint?[perResourceHistories.Count];

        for (var i = bucketCount - 1; i >= 0; i--)
        {
            var bucketEnd = now - (bucketSize * i);
            long totalActive = 0;
            long totalDeadLetter = 0;

            for (var r = 0; r < perResourceHistories.Count; r++)
            {
                var history = perResourceHistories[r];
                while (cursors[r] < history.Count && history[cursors[r]].At <= bucketEnd)
                {
                    latestPerResource[r] = history[cursors[r]];
                    cursors[r]++;
                }

                if (latestPerResource[r] is { } latest)
                {
                    totalActive += latest.ActiveCount;
                    totalDeadLetter += latest.DeadLetterCount;
                }
            }

            buckets.Add(new Bucket(bucketEnd, totalActive, totalDeadLetter));
        }

        return buckets;
    }

    /// <summary>
    /// A one-line summary of dead-letter backlog growth over the window: the total delta across
    /// every resource plus whichever single resource contributed the most of it, or an honest
    /// "no change" when the total did not grow. Deliberately does not attempt to detect *when*
    /// growth accelerated (see spec's "no fabricated numbers" constraint) -- just the delta.
    /// </summary>
    public static string SummarizeGrowth(
        IReadOnlyDictionary<string, IReadOnlyList<MetricSnapshotPoint>> historyByResourceLabel,
        TimeSpan window, DateTimeOffset now)
    {
        var windowStart = now - window;
        var deltas = new Dictionary<string, long>();

        foreach (var (label, history) in historyByResourceLabel)
        {
            var baseline = history.Where(p => p.At <= windowStart).OrderByDescending(p => p.At).FirstOrDefault();
            var latest = history.Where(p => p.At <= now).OrderByDescending(p => p.At).FirstOrDefault();
            var startCount = baseline?.DeadLetterCount ?? 0;
            var endCount = latest?.DeadLetterCount ?? 0;
            deltas[label] = endCount - startCount;
        }

        var totalDelta = deltas.Values.Sum();
        if (totalDelta <= 0)
        {
            return $"No change in the last {FormatWindow(window)}.";
        }

        var top = deltas.OrderByDescending(kv => kv.Value).First();
        var totalGrowth = deltas.Values.Where(d => d > 0).Sum();
        var pct = totalGrowth > 0 ? (int)Math.Round(top.Value * 100.0 / totalGrowth) : 0;
        return $"+{totalDelta} in the last {FormatWindow(window)} — {top.Key} accounts for {pct}% of the rise.";
    }

    private static string FormatWindow(TimeSpan window) => $"{(int)window.TotalHours}h";
}
