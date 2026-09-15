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

        for (var i = bucketCount - 1; i >= 0; i--)
        {
            var bucketEnd = now - (bucketSize * i);
            long totalActive = 0;
            long totalDeadLetter = 0;

            foreach (var history in perResourceHistories)
            {
                var latest = history.Where(p => p.At <= bucketEnd).OrderByDescending(p => p.At).FirstOrDefault();
                if (latest is not null)
                {
                    totalActive += latest.ActiveCount;
                    totalDeadLetter += latest.DeadLetterCount;
                }
            }

            buckets.Add(new Bucket(bucketEnd, totalActive, totalDeadLetter));
        }

        return buckets;
    }
}
