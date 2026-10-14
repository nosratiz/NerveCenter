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
        rows.Should().OnlyContain(r => r.Depth == 0);
    }

    private static PgActivity Mixed() => A(1, B(1), B(2, "idle"), B(3, "idle"), B(4, "idle in transaction", xactMinutesAgo: 1));

    [Fact]
    public void Default_view_hides_idle_and_counts_them()
    {
        var (rows, hidden) = ActivityTree.Build(Mixed(), false, false);

        rows.Select(r => r.Backend.Pid).Should().Equal(4, 1);
        hidden.Should().Be(2);
    }

    [Fact]
    public void Show_idle_shows_everything_with_nothing_hidden()
    {
        var (rows, hidden) = ActivityTree.Build(Mixed(), false, true);

        rows.Should().HaveCount(4);
        hidden.Should().Be(0);
    }

    [Fact]
    public void Active_only_drops_idle_and_idle_in_transaction()
    {
        var (rows, hidden) = ActivityTree.Build(Mixed(), true, false);

        rows.Select(r => r.Backend.Pid).Should().Equal(1);
        hidden.Should().Be(2);
    }

    [Fact]
    public void Active_only_wins_over_show_idle()
    {
        var (rows, hidden) = ActivityTree.Build(Mixed(), true, true);

        rows.Select(r => r.Backend.Pid).Should().Equal(1);
        hidden.Should().Be(2);
    }

    [Fact]
    public void Victims_of_a_filtered_out_blocker_appear_at_depth_zero()
    {
        var activity = A(1, B(10, "idle in transaction", xactMinutesAgo: 5), B(11, blockedBy: [10], xactMinutesAgo: 4));

        var (rows, _) = ActivityTree.Build(activity, true, false);

        rows.Select(r => (r.Backend.Pid, r.Depth)).Should().Equal((11, 0));
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
        var most = A(1,
            B(10, "idle in transaction", xactMinutesAgo: 9, durationS: 100), B(11, blockedBy: [10]),
            B(20, "idle in transaction", xactMinutesAgo: 8, durationS: 500), B(21, blockedBy: [20]),
            B(30, "idle in transaction", xactMinutesAgo: 7, durationS: 50), B(31, blockedBy: [30]), B(32, blockedBy: [30]));
        var tie = A(1,
            B(10, "idle in transaction", xactMinutesAgo: 9, durationS: 100), B(11, blockedBy: [10]),
            B(20, "idle in transaction", xactMinutesAgo: 8, durationS: 500), B(21, blockedBy: [20]));

        ActivityTree.WorstBlocker(most)!.Backend.Pid.Should().Be(30);
        ActivityTree.WorstBlocker(tie)!.Backend.Pid.Should().Be(20);
    }

    [Fact]
    public void Worst_blocker_is_null_when_nothing_blocks()
    {
        ActivityTree.WorstBlocker(A(1, B(1), B(2))).Should().BeNull();
    }

    [Fact]
    public void Worst_blocker_ignores_the_view_filter()
    {
        var activity = A(1, B(10, "idle in transaction", xactMinutesAgo: 5), B(11, blockedBy: [10]), B(12, blockedBy: [10]));

        var worst = ActivityTree.WorstBlocker(activity);

        worst!.Backend.Pid.Should().Be(10);
        worst.BlocksCount.Should().Be(2);
    }

    [Fact]
    public void TransitiveVictims_counts_the_whole_chain_below_a_blocker()
    {
        var a = A(1, B(10), B(11, blockedBy: [10]), B(12, blockedBy: [11]), B(13, blockedBy: [10, 11]));

        ActivityTree.TransitiveVictims(a, 10).Should().Be(3);
        ActivityTree.TransitiveVictims(a, 11).Should().Be(2);
        ActivityTree.TransitiveVictims(a, 12).Should().Be(0);
        ActivityTree.TransitiveVictims(a, 999).Should().Be(0);
    }

    [Fact]
    public void TransitiveVictims_terminates_on_a_cycle_and_never_counts_the_blocker_itself()
    {
        var a = A(1, B(10, blockedBy: [12]), B(11, blockedBy: [10]), B(12, blockedBy: [11]));

        ActivityTree.TransitiveVictims(a, 10).Should().Be(2);
    }

    [Fact]
    public void BlocksCount_is_transitive()
    {
        var (rows, _) = ActivityTree.Build(A(1, B(10, xactMinutesAgo: 5), B(11, blockedBy: [10], xactMinutesAgo: 4), B(12, blockedBy: [11], xactMinutesAgo: 3)), false, false);

        rows.Single(r => r.Backend.Pid == 10).BlocksCount.Should().Be(2);
        rows.Single(r => r.Backend.Pid == 11).BlocksCount.Should().Be(1);
    }
}
