using FluentAssertions;
using SbConsole.Sdk;
using SbConsole.Web.Wallboard;

namespace SbConsole.Web.Tests;

public class WallboardAggregatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T12:00:00Z");

    [Fact]
    public void BucketAndSum_sums_the_most_recent_point_at_or_before_each_bucket_across_resources()
    {
        var queueA = new List<MetricSnapshotPoint>
        {
            new(Now.AddMinutes(-3), 10, 1),
            new(Now.AddMinutes(-1), 12, 1),
        };
        var queueB = new List<MetricSnapshotPoint>
        {
            new(Now.AddMinutes(-2), 5, 0),
        };

        var buckets = WallboardAggregator.BucketAndSum(
            [queueA, queueB], TimeSpan.FromMinutes(1), Now, TimeSpan.FromMinutes(3));

        // Bucket ends: Now-2min, Now-1min, Now.
        buckets.Should().HaveCount(3);
        buckets[0].At.Should().Be(Now.AddMinutes(-2));
        buckets[0].TotalActive.Should().Be(10 + 5); // queueA's point at -3min, queueB's at -2min
        buckets[1].At.Should().Be(Now.AddMinutes(-1));
        buckets[1].TotalActive.Should().Be(12 + 5); // queueA's point at -1min, queueB still at -2min (no later point)
        buckets[2].At.Should().Be(Now);
        buckets[2].TotalActive.Should().Be(12 + 5); // no point at or before Now newer than -1min/-2min
    }

    [Fact]
    public void BucketAndSum_treats_a_resource_with_no_point_yet_as_zero()
    {
        var queueWithNoHistoryYet = new List<MetricSnapshotPoint>
        {
            new(Now.AddSeconds(-30), 7, 2), // only point is inside the last bucket
        };

        var buckets = WallboardAggregator.BucketAndSum(
            [queueWithNoHistoryYet], TimeSpan.FromMinutes(1), Now, TimeSpan.FromMinutes(2));

        buckets.Should().HaveCount(2);
        buckets[0].TotalActive.Should().Be(0); // bucket at Now-1min: no point that early yet
        buckets[0].TotalDeadLetter.Should().Be(0);
        buckets[1].TotalActive.Should().Be(7); // bucket at Now: the -30s point qualifies
        buckets[1].TotalDeadLetter.Should().Be(2);
    }

    [Fact]
    public void BucketAndSum_returns_empty_when_given_no_resources()
    {
        var buckets = WallboardAggregator.BucketAndSum([], TimeSpan.FromMinutes(1), Now, TimeSpan.FromMinutes(5));

        buckets.Should().HaveCount(5);
        buckets.Should().OnlyContain(b => b.TotalActive == 0 && b.TotalDeadLetter == 0);
    }
}
