using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests;

public class RedisPluginDashboardTests
{
    [Fact]
    public async Task Dashboard_metrics_are_empty_because_the_host_sums_same_label_metrics_across_connections()
    {
        // The host's dashboard (Home.razor) sums same-label PluginDashboardMetric values across
        // every connection of a plugin -- fine for counts, meaningless for a percentage (two Redis
        // connections at 60% and 90% memory would show "150"). The real memory-pressure signal is
        // already surfaced correctly, per-connection, via GetDashboardProblemsAsync, which is not
        // affected by summing since problems are listed individually. So this hook reports nothing
        // for now rather than a summable metric invented without a real consumer.
        var plugin = new RedisPlugin();

        var metrics = await plugin.GetDashboardMetricsAsync("standalone|0|localhost:6379");

        metrics.Should().BeEmpty();
    }

    private static ServerOverview OverviewAt(double usedPercentOfMax)
    {
        const long max = 8_000_000_000;
        var used = (long)(max * usedPercentOfMax / 100);
        return new ServerOverview("7.2.4", "standalone", 0, used, max, "allkeys-lru", 1.0, 90, 100, 0, 0, 1, 0, []);
    }

    [Fact]
    public async Task No_problem_when_memory_is_comfortably_below_the_threshold()
    {
        var plugin = new RedisPlugin();
        var operations = Substitute.For<IRedisOperations>();
        operations.GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(OverviewAt(50));

        var problems = await plugin.GetDashboardProblemsAsyncForTesting(operations, Guid.NewGuid(), "secret");

        problems.Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_a_warning_at_ninety_percent_used_memory()
    {
        var plugin = new RedisPlugin();
        var operations = Substitute.For<IRedisOperations>();
        operations.GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(OverviewAt(92));

        var problems = await plugin.GetDashboardProblemsAsyncForTesting(operations, Guid.NewGuid(), "secret");

        problems.Should().ContainSingle();
        problems[0].Severity.Should().Be("Warning");
        problems[0].LinkHref.Should().Be("/p/redis/overview");
    }

    [Fact]
    public async Task No_problem_when_maxmemory_is_unbounded()
    {
        var plugin = new RedisPlugin();
        var operations = Substitute.For<IRedisOperations>();
        operations.GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ServerOverview("7.2.4", "standalone", 0, 100, null, "noeviction", 1.0, 90, 100, 0, 0, 1, 0, []));

        var problems = await plugin.GetDashboardProblemsAsyncForTesting(operations, Guid.NewGuid(), "secret");

        problems.Should().BeEmpty();
    }

    // ---- Streams nav badge / oldest-dead-letter (design spec §2/§8) ----

    private const string Secret = "standalone|0|localhost:6379";

    [Fact]
    public void Streams_badge_is_null_not_zero_when_nothing_is_pending()
    {
        RedisPlugin.ComputeStreamsBadge(new RedisPlugin.StreamPendingOverview(0, null)).Should().BeNull();
    }

    [Fact]
    public void Streams_badge_is_the_total_pending_count()
    {
        RedisPlugin.ComputeStreamsBadge(new RedisPlugin.StreamPendingOverview(7, null)).Should().Be(7);
    }

    [Fact]
    public void Streams_badge_clamps_to_int_max()
    {
        RedisPlugin.ComputeStreamsBadge(new RedisPlugin.StreamPendingOverview(long.MaxValue, null)).Should().Be(int.MaxValue);
    }

    private static StreamScanPage OneStreamPage(params string[] keys) =>
        new(keys.Select(k => new StreamSummary(k, 10, "1696000000100-0", "1695000000000-0", "1696000000100-0")).ToList(), 0, true, 1000, 1000);

    [Fact]
    public async Task FetchStreamPendingOverviewAsync_sums_pending_across_streams_and_groups()
    {
        var operations = Substitute.For<IRedisOperations>();
        operations.ScanStreamsAsync(Secret, Arg.Any<ScanStreamsRequest>(), Arg.Any<CancellationToken>()).Returns(OneStreamPage("orders:events", "jobs:events"));
        operations.GetStreamConsumerGroupsAsync(Secret, 0, "orders:events", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumerGroupSummary>)[new StreamConsumerGroupSummary("workers", 1, 3, "1696000000000-0", 0)]);
        operations.GetStreamConsumerGroupsAsync(Secret, 0, "jobs:events", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumerGroupSummary>)[new StreamConsumerGroupSummary("workers", 1, 5, "1696000000000-0", 0)]);
        operations.GetStreamPendingEntriesAsync(Secret, 0, Arg.Any<string>(), "workers", 1, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamPendingEntry>)[new StreamPendingEntry("1696000000000-0", "worker-1", TimeSpan.FromSeconds(10), 1)]);

        var overview = await RedisPlugin.FetchStreamPendingOverviewAsync(operations, Secret, CancellationToken.None);

        overview.TotalPendingCount.Should().Be(8);
    }

    [Fact]
    public async Task FetchStreamPendingOverviewAsync_skips_groups_with_no_pending_entries()
    {
        var operations = Substitute.For<IRedisOperations>();
        operations.ScanStreamsAsync(Secret, Arg.Any<ScanStreamsRequest>(), Arg.Any<CancellationToken>()).Returns(OneStreamPage("orders:events"));
        operations.GetStreamConsumerGroupsAsync(Secret, 0, "orders:events", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumerGroupSummary>)[new StreamConsumerGroupSummary("workers", 1, 0, "1696000000000-0", 0)]);

        var overview = await RedisPlugin.FetchStreamPendingOverviewAsync(operations, Secret, CancellationToken.None);

        overview.TotalPendingCount.Should().Be(0);
        overview.Oldest.Should().BeNull();
        await operations.DidNotReceive().GetStreamPendingEntriesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchStreamPendingOverviewAsync_picks_the_entry_with_the_largest_idle_as_oldest()
    {
        var operations = Substitute.For<IRedisOperations>();
        operations.ScanStreamsAsync(Secret, Arg.Any<ScanStreamsRequest>(), Arg.Any<CancellationToken>()).Returns(OneStreamPage("orders:events", "jobs:events"));
        operations.GetStreamConsumerGroupsAsync(Secret, 0, "orders:events", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumerGroupSummary>)[new StreamConsumerGroupSummary("workers", 1, 1, "1696000000000-0", 0)]);
        operations.GetStreamConsumerGroupsAsync(Secret, 0, "jobs:events", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumerGroupSummary>)[new StreamConsumerGroupSummary("workers", 1, 4, "1696000000000-0", 0)]);
        operations.GetStreamPendingEntriesAsync(Secret, 0, "orders:events", "workers", 1, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamPendingEntry>)[new StreamPendingEntry("1696000000000-0", "worker-1", TimeSpan.FromMinutes(1), 1)]);
        operations.GetStreamPendingEntriesAsync(Secret, 0, "jobs:events", "workers", 1, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamPendingEntry>)[new StreamPendingEntry("1696000000001-0", "worker-2", TimeSpan.FromMinutes(30), 1)]);

        var overview = await RedisPlugin.FetchStreamPendingOverviewAsync(operations, Secret, CancellationToken.None);

        overview.Oldest.Should().NotBeNull();
        overview.Oldest!.ResourceName.Should().Be("jobs:events/workers");
        overview.Oldest.DeadLetterCount.Should().Be(4);
    }

    [Fact]
    public async Task FetchStreamPendingOverviewAsync_returns_null_oldest_when_no_stream_has_a_group()
    {
        var operations = Substitute.For<IRedisOperations>();
        operations.ScanStreamsAsync(Secret, Arg.Any<ScanStreamsRequest>(), Arg.Any<CancellationToken>()).Returns(OneStreamPage("orders:events"));
        operations.GetStreamConsumerGroupsAsync(Secret, 0, "orders:events", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<StreamConsumerGroupSummary>)[]);

        var overview = await RedisPlugin.FetchStreamPendingOverviewAsync(operations, Secret, CancellationToken.None);

        overview.TotalPendingCount.Should().Be(0);
        overview.Oldest.Should().BeNull();
    }

    [Fact]
    public async Task GetCachedStreamPendingOverviewAsync_reuses_the_result_within_the_TTL()
    {
        var secret = $"cache-reuse-{Guid.NewGuid()}";
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        Task<RedisPlugin.StreamPendingOverview> Fetch(string s, CancellationToken t) { calls++; return Task.FromResult(new RedisPlugin.StreamPendingOverview(1, null)); }

        var first = await RedisPlugin.GetCachedStreamPendingOverviewAsync(secret, now, Fetch, CancellationToken.None);
        var second = await RedisPlugin.GetCachedStreamPendingOverviewAsync(secret, now.AddSeconds(59), Fetch, CancellationToken.None);

        calls.Should().Be(1);
        second.Should().Be(first);
    }

    [Fact]
    public async Task GetCachedStreamPendingOverviewAsync_fetches_again_after_the_TTL()
    {
        var secret = $"cache-expiry-{Guid.NewGuid()}";
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        Task<RedisPlugin.StreamPendingOverview> Fetch(string s, CancellationToken t) { calls++; return Task.FromResult(new RedisPlugin.StreamPendingOverview(calls, null)); }

        await RedisPlugin.GetCachedStreamPendingOverviewAsync(secret, now, Fetch, CancellationToken.None);
        await RedisPlugin.GetCachedStreamPendingOverviewAsync(secret, now.AddSeconds(61), Fetch, CancellationToken.None);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetCachedStreamPendingOverviewAsync_never_caches_a_failure()
    {
        var secret = $"cache-failure-{Guid.NewGuid()}";
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        Task<RedisPlugin.StreamPendingOverview> Fetch(string s, CancellationToken t)
        {
            calls++;
            return calls == 1 ? Task.FromException<RedisPlugin.StreamPendingOverview>(new InvalidOperationException("boom")) : Task.FromResult(new RedisPlugin.StreamPendingOverview(0, null));
        }

        var act = () => RedisPlugin.GetCachedStreamPendingOverviewAsync(secret, now, Fetch, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var retried = await RedisPlugin.GetCachedStreamPendingOverviewAsync(secret, now.AddSeconds(1), Fetch, CancellationToken.None);

        calls.Should().Be(2);
        retried.TotalPendingCount.Should().Be(0);
    }

    [Fact]
    public void Oldest_dead_letter_scope_is_documented_as_the_connections_default_database_only()
    {
        // FetchStreamPendingOverviewAsync scans RedisConnectionSettings.Parse(secret).DefaultDatabase
        // only, not every database 0-15 -- Redis has no single cross-database call the way
        // RabbitMQ's management API covers every vhost in one request (see the comment on
        // FetchStreamPendingOverviewAsync). This test exists only to anchor that scoping decision to
        // a real assertion: parsing the secret's own DefaultDatabase is what selects the database.
        RedisConnectionSettings.Parse("standalone|0|localhost:6379,defaultDatabase=3").DefaultDatabase.Should().Be(3);
    }
}
