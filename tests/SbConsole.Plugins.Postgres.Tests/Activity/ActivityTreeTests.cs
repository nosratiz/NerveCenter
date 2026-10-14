using FluentAssertions;
using SbConsole.Plugins.Postgres.Activity;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Activity;

public class ActivityTreeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static PgBackend B(int pid, string state = "active", int[]? blockedBy = null, int xactMinutesAgo = -1, int? durationS = null) =>
        new(pid, "u", "app", "10.0.0.1", state, null, "select 1",
            xactMinutesAgo < 0 ? null : T0.AddMinutes(-xactMinutesAgo), null,
            durationS is null ? null : TimeSpan.FromSeconds(durationS.Value), blockedBy ?? [], null, "app");

    private static PgActivity A(int own, params PgBackend[] b) => new(own, T0, b);

    [Fact]
    public void Victims_follow_their_blocker_indented()
    {
        var (rows, _) = ActivityTree.Build(A(1, B(10, xactMinutesAgo: 5), B(11, blockedBy: [10], xactMinutesAgo: 4), B(12, xactMinutesAgo: 1)), false, false);

        rows.Select(r => (r.Backend.Pid, r.Depth)).Should().Equal((10, 0), (11, 1), (12, 0));
        rows[0].BlocksCount.Should().Be(1);
    }

    [Fact]
    public void Top_level_orders_by_xact_start_nulls_last_then_pid()
    {
        var (rows, _) = ActivityTree.Build(A(1, B(9), B(8), B(7, xactMinutesAgo: 1), B(6, xactMinutesAgo: 9)), false, false);

        rows.Select(r => r.Backend.Pid).Should().Equal(6, 7, 8, 9);
    }

    [Fact]
    public void Chains_nest_to_deeper_levels()
    {
        var (rows, _) = ActivityTree.Build(A(1, B(1, blockedBy: [2], xactMinutesAgo: 1), B(2, blockedBy: [3], xactMinutesAgo: 2), B(3, xactMinutesAgo: 3)), false, false);

        rows.Select(r => (r.Backend.Pid, r.Depth)).Should().Equal((3, 0), (2, 1), (1, 2));
    }

    [Fact]
    public void A_victim_sits_under_its_first_blocker_only_and_appears_once()
    {
        var (rows, _) = ActivityTree.Build(A(1, B(10, xactMinutesAgo: 9), B(20, xactMinutesAgo: 8), B(30, blockedBy: [10, 20])), false, false);

        rows.Select(r => (r.Backend.Pid, r.Depth)).Should().Equal((10, 0), (30, 1), (20, 0));
        rows.Single(r => r.Backend.Pid == 20).BlocksCount.Should().Be(1);
    }

    [Fact]
    public void A_blocking_cycle_falls_back_to_depth_zero()
    {
        var (rows, _) = ActivityTree.Build(A(1, B(10, blockedBy: [11], xactMinutesAgo: 2), B(11, blockedBy: [10], xactMinutesAgo: 1), B(12)), false, false);

        rows.Should().HaveCount(3);
        rows.Select(r => r.Backend.Pid).Should().BeEquivalentTo([10, 11, 12]);
        rows.Single(r => r.Backend.Pid == 12).Depth.Should().Be(0);
        rows.Select(r => r.Depth).Max().Should().BeLessThan(3);
    }

    [Fact]
    public void Active_only_hides_idle_and_counts_them()
    {
        var activity = A(1, B(1), B(2, "idle"), B(3, "idle"), B(4, "idle in transaction", xactMinutesAgo: 1));

        var (hiddenRows, hidden) = ActivityTree.Build(activity, true, false);
        var (shownRows, none) = ActivityTree.Build(activity, true, true);
        var (allRows, noneAll) = ActivityTree.Build(activity, false, false);

        hiddenRows.Select(r => r.Backend.Pid).Should().Equal(4, 1);
        hidden.Should().Be(2);
        shownRows.Should().HaveCount(4);
        none.Should().Be(0);
        allRows.Should().HaveCount(4);
        noneAll.Should().Be(0);
    }

    [Fact]
    public void Own_pid_is_flagged()
    {
        var (rows, _) = ActivityTree.Build(A(2, B(1), B(2)), false, false);

        rows.Single(r => r.IsOwn).Backend.Pid.Should().Be(2);
    }

    [Fact]
    public void Worst_blocker_has_most_victims_then_longest_duration()
    {
        var (rows, _) = ActivityTree.Build(A(1,
            B(10, "idle in transaction", xactMinutesAgo: 9, durationS: 100), B(11, blockedBy: [10]),
            B(20, "idle in transaction", xactMinutesAgo: 8, durationS: 500), B(21, blockedBy: [20]),
            B(30, "idle in transaction", xactMinutesAgo: 7, durationS: 50), B(31, blockedBy: [30]), B(32, blockedBy: [30])), false, false);

        ActivityTree.WorstBlocker(rows)!.Backend.Pid.Should().Be(30);
        var tie = rows.Where(r => r.Backend.Pid != 30 && r.Backend.Pid != 31 && r.Backend.Pid != 32).ToList();
        ActivityTree.WorstBlocker(tie)!.Backend.Pid.Should().Be(20);
    }

    [Fact]
    public void Worst_blocker_is_null_when_nothing_blocks()
    {
        var (rows, _) = ActivityTree.Build(A(1, B(1), B(2)), false, false);

        ActivityTree.WorstBlocker(rows).Should().BeNull();
    }
}
