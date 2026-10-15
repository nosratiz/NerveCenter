using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Overview;
using static SbConsole.Plugins.Kubernetes.Tests.TestData;

namespace SbConsole.Plugins.Kubernetes.Tests.Overview;

public class OverviewSnapshotStoreTests
{
    private static readonly Guid Id = Guid.NewGuid();

    [Fact]
    public async Task Empty_store_reads_as_empty_state() =>
        (await OverviewSnapshotStore.ReadAsync(new InMemoryPluginStore(), Id, default)).Should().Be(new OverviewState(null, null, null));

    [Fact]
    public async Task Populated_overview_round_trips_through_json()
    {
        var store = new InMemoryPluginStore();
        var overview = OverviewWith(
            nodes: [Node("n1"), Node("n2", ready: "False") with { Pressures = ["DiskPressure"], Taints = ["a=b:NoSchedule"], Labels = new Dictionary<string, string> { ["agentpool"] = "system" } }],
            problemPods: [Pod(status: "CrashLoopBackOff", restarts: 4, lastReason: "OOMKilled", lastExit: 137), Pod(name: "p", phase: "Pending", unschedulable: "no room")],
            problemWorkloads: [Job(), Deployment(available: 1, conditions: [new K8sCondition("Available", "False", "MinimumReplicasUnavailable", "m", T0)])],
            warnings: [Warning(), Warning(reason: "FailedScheduling")]) with
        {
            CpuUsageFraction = 0.42, MemUsageFraction = 0.17, CpuLimitsMilli = 1200, MemLimitsBytes = 1L << 31, NodesForbidden = true,
        };
        var snapshot = new OverviewSnapshot(overview, T0.AddMinutes(1), ["checkout", "payments"]);

        await OverviewSnapshotStore.WriteGoodAsync(store, Id, snapshot, default);
        var state = await OverviewSnapshotStore.ReadAsync(store, Id, default);

        store.Values.Should().ContainKey($"overview:{Id}");
        state.LastGood.Should().NotBeNull();
        state.LastGood.Should().BeEquivalentTo(snapshot);
        state.LastGood!.Overview.ProblemWorkloads[1].IsUnhealthy.Should().BeTrue();
        state.LastGood.Overview.Nodes[1].Labels["agentpool"].Should().Be("system");
        state.LastGood.Overview.NodesForbidden.Should().BeTrue();
        state.LastFailureAt.Should().BeNull();
    }

    [Fact]
    public async Task Failure_keeps_the_last_good_snapshot()
    {
        var store = new InMemoryPluginStore();
        var snapshot = new OverviewSnapshot(OverviewWith(), T0, []);
        await OverviewSnapshotStore.WriteGoodAsync(store, Id, snapshot, default);

        await OverviewSnapshotStore.WriteFailureAsync(store, Id, T0.AddMinutes(3), "Can't reach the cluster.", default);
        var state = await OverviewSnapshotStore.ReadAsync(store, Id, default);

        state.LastGood.Should().BeEquivalentTo(snapshot);
        state.LastFailureAt.Should().Be(T0.AddMinutes(3));
        state.LastError.Should().Be("Can't reach the cluster.");
    }

    [Fact]
    public async Task Corrupt_json_reads_as_empty()
    {
        var store = new InMemoryPluginStore();
        store.Values[OverviewSnapshotStore.Key(Id)] = "{not json";

        (await OverviewSnapshotStore.ReadAsync(store, Id, default)).LastGood.Should().BeNull();
    }
}
