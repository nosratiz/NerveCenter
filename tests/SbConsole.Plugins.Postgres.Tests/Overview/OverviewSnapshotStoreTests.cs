using FluentAssertions;
using SbConsole.Plugins.Postgres.Overview;

namespace SbConsole.Plugins.Postgres.Tests.Overview;

public class OverviewSnapshotStoreTests
{
    private static readonly Guid Id = Guid.NewGuid();

    [Fact]
    public async Task Empty_store_reads_as_empty_state()
    {
        var state = await OverviewSnapshotStore.ReadAsync(new InMemoryPluginStore(), Id, default);

        state.Should().Be(new OverviewState(null, null, null));
    }

    [Fact]
    public async Task Good_snapshot_round_trips_timespans_dates_and_nested_lists()
    {
        var store = new InMemoryPluginStore();
        var attention = OverviewTestData.NoAttention() with
        {
            BlockedCount = 2, LongestBlockedFor = TimeSpan.FromSeconds(95), TopBlockerPid = 7, TopBlockerRelation = "public.t",
            IdleInTransaction = [new(11, "u", "a", TimeSpan.FromMinutes(9))],
            DeadTupleTables = [new("public.big", 42.5)],
            StatsResetAt = OverviewTestData.T0,
        };
        var snapshot = new OverviewSnapshot(
            OverviewTestData.Overview(attention: attention, replication: new(1, TimeSpan.FromSeconds(31), "r1", 1024)),
            OverviewTestData.T0.AddMinutes(1));

        await OverviewSnapshotStore.WriteGoodAsync(store, Id, snapshot, default);
        var state = await OverviewSnapshotStore.ReadAsync(store, Id, default);

        state.LastGood.Should().BeEquivalentTo(snapshot);
        state.LastFailureAt.Should().BeNull();
        store.Values.Keys.Should().Contain($"overview:{Id}");
    }

    [Fact]
    public async Task Failure_is_recorded_and_keeps_last_good()
    {
        var store = new InMemoryPluginStore();
        var snapshot = new OverviewSnapshot(OverviewTestData.Overview(), OverviewTestData.T0);
        await OverviewSnapshotStore.WriteGoodAsync(store, Id, snapshot, default);

        await OverviewSnapshotStore.WriteFailureAsync(store, Id, OverviewTestData.T0.AddMinutes(5), "boom", default);
        var state = await OverviewSnapshotStore.ReadAsync(store, Id, default);

        state.LastGood.Should().BeEquivalentTo(snapshot);
        state.LastFailureAt.Should().Be(OverviewTestData.T0.AddMinutes(5));
        state.LastError.Should().Be("boom");
    }

    [Fact]
    public async Task Good_write_clears_a_previous_failure()
    {
        var store = new InMemoryPluginStore();
        await OverviewSnapshotStore.WriteFailureAsync(store, Id, OverviewTestData.T0, "boom", default);

        await OverviewSnapshotStore.WriteGoodAsync(store, Id, new OverviewSnapshot(OverviewTestData.Overview(), OverviewTestData.T0), default);
        var state = await OverviewSnapshotStore.ReadAsync(store, Id, default);

        state.LastFailureAt.Should().BeNull();
        state.LastError.Should().BeNull();
        state.LastGood.Should().NotBeNull();
    }

    [Fact]
    public async Task Corrupt_json_reads_as_empty_state()
    {
        var store = new InMemoryPluginStore();
        store.Values[$"overview:{Id}"] = "{not json";

        (await OverviewSnapshotStore.ReadAsync(store, Id, default)).Should().Be(new OverviewState(null, null, null));
    }
}
