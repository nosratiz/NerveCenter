using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests;

public class PostgresPluginDashboardTests
{
    private static PgAttention Att(Func<PgAttention, PgAttention> f) => f(OverviewTestData.NoAttention());

    private static IReadOnlyList<PluginDashboardProblem> Problems(PgOverview o) => PostgresPlugin.ComputeProblems(o);

    [Fact]
    public async Task Metrics_report_one_server()
    {
        var metrics = await new PostgresPlugin().GetDashboardMetricsAsync("x");

        metrics.Should().Equal(new PluginDashboardMetric("Servers", 1));
    }

    [Fact]
    public void Healthy_overview_has_no_problems() =>
        Problems(OverviewTestData.Overview()).Should().BeEmpty();

    [Fact]
    public void Blocked_longer_than_a_minute_is_an_error_with_blocker_and_relation()
    {
        var o = OverviewTestData.Overview(attention: Att(a => a with
        {
            BlockedCount = 3, LongestBlockedFor = TimeSpan.FromSeconds(61), TopBlockerPid = 99, TopBlockerRelation = "public.orders",
        }));

        Problems(o).Should().Equal(new PluginDashboardProblem("Error", "3 sessions blocked", "blocker pid 99 on public.orders", PostgresPlugin.ActivityHref));
    }

    [Fact]
    public void Single_blocked_session_is_singular_and_relation_is_optional()
    {
        var o = OverviewTestData.Overview(attention: Att(a => a with
        {
            BlockedCount = 1, LongestBlockedFor = TimeSpan.FromMinutes(2), TopBlockerPid = 5,
        }));

        Problems(o).Should().ContainSingle().Which.Should().Be(
            new PluginDashboardProblem("Error", "1 session blocked", "blocker pid 5", PostgresPlugin.ActivityHref));
    }

    [Fact]
    public void Blocked_for_exactly_a_minute_is_not_a_problem()
    {
        var o = OverviewTestData.Overview(attention: Att(a => a with
        {
            BlockedCount = 2, LongestBlockedFor = TimeSpan.FromMinutes(1), TopBlockerPid = 5,
        }));

        Problems(o).Should().BeEmpty();
    }

    [Fact]
    public void Idle_in_transaction_is_a_warning_with_extra_count()
    {
        var one = OverviewTestData.Overview(attention: Att(a => a with
        {
            IdleInTransaction = [new(11, "u", "a", TimeSpan.FromMinutes(9))],
        }));
        var three = OverviewTestData.Overview(attention: Att(a => a with
        {
            IdleInTransaction = [new(11, "u", "a", TimeSpan.FromMinutes(9)), new(12, "u", "a", TimeSpan.FromMinutes(8)), new(13, "u", "a", TimeSpan.FromMinutes(6))],
        }));

        Problems(one).Should().Equal(new PluginDashboardProblem("Warning", "Idle in transaction",
            $"pid 11 for {PgFormat.Duration(TimeSpan.FromMinutes(9))}", PostgresPlugin.ActivityHref));
        Problems(three).Single().Detail.Should().Be($"pid 11 for {PgFormat.Duration(TimeSpan.FromMinutes(9))} and 2 more");
    }

    [Theory]
    [InlineData(90, 100, true)]
    [InlineData(89, 100, false)]
    [InlineData(179, 200, false)]
    [InlineData(180, 200, true)]
    public void Connections_at_ninety_percent_flag_but_eighty_nine_does_not(int connections, int max, bool flagged)
    {
        var o = OverviewTestData.Overview(connections: connections, maxConnections: max);

        var problems = Problems(o);

        if (flagged)
        {
            problems.Should().Equal(new PluginDashboardProblem("Warning", "Connections at 90%", $"{connections} / {max} max_connections", PostgresPlugin.OverviewHref));
        }
        else
        {
            problems.Should().BeEmpty();
        }
    }

    [Fact]
    public void Zero_max_connections_does_not_divide_by_zero() =>
        Problems(OverviewTestData.Overview(connections: 5, maxConnections: 0)).Should().BeEmpty();

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public void Replication_lag_over_thirty_seconds_warns(int seconds, bool flagged)
    {
        var o = OverviewTestData.Overview(replication: new PgReplication(1, TimeSpan.FromSeconds(seconds), "replica-1", null));

        var problems = Problems(o);

        if (flagged)
        {
            problems.Should().Equal(new PluginDashboardProblem("Warning", "Replication lag",
                $"{PgFormat.Duration(TimeSpan.FromSeconds(seconds))} on replica-1", PostgresPlugin.OverviewHref));
        }
        else
        {
            problems.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(1_500_000_000L, false)]
    [InlineData(1_500_000_001L, true)]
    public void Xid_age_over_one_and_a_half_billion_is_an_error(long age, bool flagged)
    {
        var problems = Problems(OverviewTestData.Overview(xidAge: age));

        if (flagged)
        {
            problems.Should().Equal(new PluginDashboardProblem("Error", "Transaction ID wraparound",
                $"oldest xid age {PgFormat.Count(age)}", PostgresPlugin.MaintenanceHref));
        }
        else
        {
            problems.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Dashboard_problems_are_empty_before_initialize_and_computed_after()
    {
        var plugin = new PostgresPlugin();
        (await plugin.GetDashboardProblemsAsync(Guid.NewGuid(), "s", new InMemoryPluginStore())).Should().BeEmpty();

        var ops = Substitute.For<IPostgresOperations>();
        ops.GetOverviewAsync("s", Arg.Any<CancellationToken>()).Returns(OverviewTestData.Overview(xidAge: 1_600_000_000));
        plugin.InitializeForTesting(ops);

        (await plugin.GetDashboardProblemsAsync(Guid.NewGuid(), "s", new InMemoryPluginStore()))
            .Should().ContainSingle().Which.Title.Should().Be("Transaction ID wraparound");
    }

    [Fact]
    public async Task Dashboard_problems_propagate_operation_failures()
    {
        var plugin = new PostgresPlugin();
        var ops = Substitute.For<IPostgresOperations>();
        ops.GetOverviewAsync("s", Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        plugin.InitializeForTesting(ops);

        var act = () => plugin.GetDashboardProblemsAsync(Guid.NewGuid(), "s", new InMemoryPluginStore());

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Nav_badge_shows_blocked_count_only_on_activity()
    {
        var plugin = new PostgresPlugin();
        var ops = Substitute.For<IPostgresOperations>();
        ops.GetBlockedCountAsync("s", Arg.Any<CancellationToken>()).Returns(4);
        plugin.InitializeForTesting(ops);

        (await plugin.GetNavBadgeAsync(PostgresPlugin.ActivityHref, "s")).Should().Be(4);
        (await plugin.GetNavBadgeAsync(PostgresPlugin.OverviewHref, "s")).Should().BeNull();
        await ops.Received(1).GetBlockedCountAsync("s", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nav_badge_is_null_when_zero_uninitialized_or_throwing()
    {
        (await new PostgresPlugin().GetNavBadgeAsync(PostgresPlugin.ActivityHref, "s")).Should().BeNull();

        var plugin = new PostgresPlugin();
        var ops = Substitute.For<IPostgresOperations>();
        ops.GetBlockedCountAsync("s", Arg.Any<CancellationToken>()).Returns(0);
        plugin.InitializeForTesting(ops);
        (await plugin.GetNavBadgeAsync(PostgresPlugin.ActivityHref, "s")).Should().BeNull();

        ops.GetBlockedCountAsync("s", Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        (await plugin.GetNavBadgeAsync(PostgresPlugin.ActivityHref, "s")).Should().BeNull();
    }
}
