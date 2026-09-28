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
}
