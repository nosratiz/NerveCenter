using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Nodes;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;
using static SbConsole.Plugins.Kubernetes.Tests.Nodes.DrainPlannerTests;

namespace SbConsole.Plugins.Kubernetes.Tests.Nodes;

public class DrainHandlerTests
{
    private const string Secret = "secret";
    private const string Node = "aks-pool1-38211-vmss00000a";
    private const string Target = "aks-prod-eu/" + Node;
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _id = Guid.NewGuid();
    private readonly RecordingProgress _progress = new();

    public DrainHandlerTests()
    {
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);
        _ops.EvictPodAsync(Secret, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(K8sEvictionOutcome.Evicted);
    }

    private DrainNodeCommandHandler Handler() =>
        new(_ops, _connections, _audit, _time, NullLogger<DrainNodeCommandHandler>.Instance);

    private DrainCommand Command(bool emptyDir = false, bool force = false, int grace = 30) =>
        new(new K8sTarget(_id, "aks-prod-eu", null, Node), new DrainOptions(emptyDir, force, grace));

    private void Arrange(params K8sPodSummary[] pods) => Arrange(pods, []);

    private void Arrange(IReadOnlyList<K8sPodSummary> pods, IReadOnlyList<K8sPdb> pdbs) =>
        _ops.GetDrainInputsAsync(Secret, Node, Arg.Any<CancellationToken>()).Returns(Inputs(pods, pdbs));

    private static K8sPodSummary Api(int i) => Pod("checkout", $"api-{i}", "Deployment", "api");

    private int EvictCalls(string? name = null) =>
        _ops.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.EvictPodAsync)
                                        && (name is null || (string)c.GetArguments()[2]! == name));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met.");
            }

            await Task.Delay(1);
        }
    }

    private string? AuditDetail() =>
        (string?)_audit.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IAuditScope.RecordAsync)).GetArguments()[4];

    private bool AuditSucceeded() =>
        (bool)_audit.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IAuditScope.RecordAsync)).GetArguments()[3]!;

    [Fact]
    public async Task Cordons_first_then_evicts_every_non_daemonset_pod_and_audits_success()
    {
        Arrange(Api(1), Api(2), Pod("kube-system", "kube-proxy-1", "DaemonSet", "kube-proxy"));

        var result = await Handler().HandleAsync(Command(grace: 45), _progress);

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _ops.SetUnschedulableAsync(Secret, Node, true, Arg.Any<CancellationToken>());
            _ops.GetDrainInputsAsync(Secret, Node, Arg.Any<CancellationToken>());
            _ops.EvictPodAsync(Secret, "checkout", "api-1", 45, Arg.Any<CancellationToken>());
            _ops.EvictPodAsync(Secret, "checkout", "api-2", 45, Arg.Any<CancellationToken>());
        });
        await _ops.DidNotReceive().EvictPodAsync(Secret, "kube-system", "kube-proxy-1", Arg.Any<int>(), Arg.Any<CancellationToken>());
        result.Value!.Pods.Should().HaveCount(2).And.OnlyContain(p => p.Outcome == DrainPodOutcome.Evicted);
        result.Value.Cancelled.Should().BeFalse();
        result.Value.TimedOut.Should().BeFalse();
        await _audit.Received(1).RecordAsync("k8s.node.drain", Target, ActionRisk.Destructive, true,
            "grace 45s · force false · emptyDir false · evicted 2 · deleted 0 · blocked 0 · skipped 0 · pods: checkout/api-1, checkout/api-2",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Evicts_at_most_five_pods_at_a_time()
    {
        Arrange(Enumerable.Range(1, 7).Select(Api).ToArray());
        var pending = new Dictionary<string, TaskCompletionSource<K8sEvictionOutcome>>();
        _ops.EvictPodAsync(Secret, "checkout", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var tcs = new TaskCompletionSource<K8sEvictionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pending)
                {
                    pending[ci.ArgAt<string>(2)] = tcs;
                }

                return tcs.Task;
            });

        var run = Handler().HandleAsync(Command(), _progress);

        await WaitForAsync(() => EvictCalls() == 5);
        await Task.Delay(50);
        EvictCalls().Should().Be(5);

        lock (pending)
        {
            pending["api-1"].SetResult(K8sEvictionOutcome.Evicted);
        }

        await WaitForAsync(() => EvictCalls() == 6);
        lock (pending)
        {
            foreach (var tcs in pending.Values)
            {
                tcs.TrySetResult(K8sEvictionOutcome.Evicted);
            }
        }

        await WaitForAsync(() => EvictCalls() == 7);
        lock (pending)
        {
            pending["api-7"].TrySetResult(K8sEvictionOutcome.Evicted);
        }

        (await run).Value!.Pods.Should().HaveCount(7);
    }

    [Fact]
    public async Task A_pdb_blocked_pod_is_retried_every_five_seconds_then_reported_blocked_at_the_five_minute_deadline()
    {
        Arrange(Pod("payments", "ledger-1", "StatefulSet", "ledger"));
        _ops.EvictPodAsync(Secret, "payments", "ledger-1", 30, Arg.Any<CancellationToken>()).Returns(K8sEvictionOutcome.BlockedByPdb);

        var run = Handler().HandleAsync(Command(), _progress);
        for (var i = 1; i < 60; i++)
        {
            await WaitForAsync(() => EvictCalls() == i);
            _time.Advance(TimeSpan.FromSeconds(4));
            await Task.Delay(5);
            EvictCalls().Should().Be(i, "the retry waits the full 5 s");
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await WaitForAsync(() => EvictCalls() == 60);
        _time.Advance(TimeSpan.FromSeconds(5));
        var result = await run;

        result.Value!.TimedOut.Should().BeTrue();
        result.Value.Cancelled.Should().BeFalse();
        result.Value.Pods.Single().Outcome.Should().Be(DrainPodOutcome.Blocked);
        EvictCalls().Should().Be(60);
        await _ops.DidNotReceiveWithAnyArgs().DeletePodAsync(default!, default!, default!, default);
        AuditSucceeded().Should().BeFalse();
        AuditDetail().Should().StartWith("grace 30s · force false · emptyDir false · evicted 0 · deleted 0 · blocked 1 · skipped 0 · timed out after 1 of 1");
    }

    [Fact]
    public async Task Deadline_can_be_shortened()
    {
        Arrange(Pod("payments", "ledger-1", "StatefulSet", "ledger"));
        _ops.EvictPodAsync(Secret, "payments", "ledger-1", 30, Arg.Any<CancellationToken>()).Returns(K8sEvictionOutcome.BlockedByPdb);
        var handler = Handler();
        handler.Deadline = TimeSpan.FromSeconds(20);

        var run = handler.HandleAsync(Command(), _progress);
        for (var i = 1; i <= 4; i++)
        {
            await WaitForAsync(() => EvictCalls() == i);
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        (await run).Value!.Pods.Single().Outcome.Should().Be(DrainPodOutcome.Blocked);
    }

    [Fact]
    public async Task Force_deletes_a_pdb_blocked_pod_with_the_grace_period()
    {
        Arrange(Pod("payments", "ledger-1", "StatefulSet", "ledger"), Api(1));
        _ops.EvictPodAsync(Secret, "payments", "ledger-1", 10, Arg.Any<CancellationToken>()).Returns(K8sEvictionOutcome.BlockedByPdb);

        var result = await Handler().HandleAsync(Command(force: true, grace: 10), _progress);

        await _ops.Received(1).DeletePodAsync(Secret, "payments", "ledger-1", 10, Arg.Any<CancellationToken>());
        result.Value!.Pods.Single(p => p.Name == "ledger-1").Outcome.Should().Be(DrainPodOutcome.Deleted);
        AuditSucceeded().Should().BeTrue();
        AuditDetail().Should().Be("grace 10s · force true · emptyDir false · evicted 1 · deleted 1 · blocked 0 · skipped 0 · pods: checkout/api-1, payments/ledger-1");
    }

    [Fact]
    public async Task EmptyDir_pods_are_skipped_unless_opted_in()
    {
        Arrange(Pod("checkout", "cache-1", "Deployment", "cache", emptyDir: true));

        var skipped = await Handler().HandleAsync(Command(), _progress);

        skipped.Value!.Pods.Single().Should().Be(new DrainPodResult("checkout", "cache-1", DrainPodOutcome.Skipped, "has emptyDir data"));
        await _ops.DidNotReceiveWithAnyArgs().EvictPodAsync(default!, default!, default!, default, default);
        AuditSucceeded().Should().BeFalse();

        var evicted = await Handler().HandleAsync(Command(emptyDir: true), _progress);

        evicted.Value!.Pods.Single().Outcome.Should().Be(DrainPodOutcome.Evicted);
    }

    [Fact]
    public async Task Unmanaged_pods_need_force()
    {
        Arrange(Pod("default", "debug-shell", null, null));

        var skipped = await Handler().HandleAsync(Command(), _progress);
        skipped.Value!.Pods.Single().Should().Be(new DrainPodResult("default", "debug-shell", DrainPodOutcome.Skipped, "no controller — use force"));

        var forced = await Handler().HandleAsync(Command(force: true), _progress);
        forced.Value!.Pods.Single().Outcome.Should().Be(DrainPodOutcome.Evicted);
    }

    [Fact]
    public async Task A_pod_already_gone_counts_as_evicted()
    {
        Arrange(Api(1));
        _ops.EvictPodAsync(Secret, "checkout", "api-1", 30, Arg.Any<CancellationToken>()).Returns(K8sEvictionOutcome.NotFound);

        var result = await Handler().HandleAsync(Command(), _progress);

        result.Value!.Pods.Single().Should().Be(new DrainPodResult("checkout", "api-1", DrainPodOutcome.Evicted, "already gone"));
    }

    [Fact]
    public async Task A_failed_eviction_is_reported_with_the_friendly_message_and_the_rest_continue()
    {
        Arrange(Api(1), Api(2));
        _ops.EvictPodAsync(Secret, "checkout", "api-1", 30, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("connection refused"));

        var result = await Handler().HandleAsync(Command(), _progress);

        var failed = result.Value!.Pods.Single(p => p.Name == "api-1");
        failed.Outcome.Should().Be(DrainPodOutcome.Failed);
        failed.Reason.Should().NotBeNullOrEmpty();
        result.Value.Pods.Single(p => p.Name == "api-2").Outcome.Should().Be(DrainPodOutcome.Evicted);
        AuditSucceeded().Should().BeFalse();
    }

    [Fact]
    public async Task Progress_is_reported_per_pod()
    {
        Arrange(Api(1), Api(2), Api(3));

        await Handler().HandleAsync(Command(), _progress);

        var perPod = _progress.Reports.Where(r => r.Last is not null).ToList();
        perPod.Should().HaveCount(3);
        perPod.Select(r => r.Done).Order().Should().Equal(1, 2, 3);
        perPod.Should().OnlyContain(r => r.Total == 3);
    }

    [Fact]
    public async Task Cancellation_stops_scheduling_leaves_the_node_cordoned_and_is_still_audited()
    {
        Arrange(Enumerable.Range(1, 7).Select(Api).ToArray());
        var pending = new List<TaskCompletionSource<K8sEvictionOutcome>>();
        _ops.EvictPodAsync(Secret, "checkout", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var tcs = new TaskCompletionSource<K8sEvictionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pending)
                {
                    pending.Add(tcs);
                }

                return tcs.Task;
            });
        using var cts = new CancellationTokenSource();

        var run = Handler().HandleAsync(Command(), _progress, cts.Token);
        await WaitForAsync(() => EvictCalls() == 5);
        await cts.CancelAsync();
        lock (pending)
        {
            pending.ForEach(t => t.SetResult(K8sEvictionOutcome.Evicted));
        }

        var result = await run;

        result.Value!.Cancelled.Should().BeTrue();
        result.Value.Pods.Should().HaveCount(5);
        EvictCalls().Should().Be(5);
        await _ops.DidNotReceive().SetUnschedulableAsync(Secret, Node, false, Arg.Any<CancellationToken>());
        AuditSucceeded().Should().BeFalse();
        AuditDetail().Should().StartWith("grace 30s · force false · emptyDir false · evicted 5 · deleted 0 · blocked 0 · skipped 0 · cancelled after 5 of 7 · pods: ");
    }

    private static K8sPodSummary Ledger(int i) =>
        Pod("payments", $"ledger-{i}", "StatefulSet", "ledger", new Dictionary<string, string> { ["app"] = "ledger" });

    private static readonly K8sPdb LedgerPdb =
        new("payments", "ledger", new Dictionary<string, string> { ["app"] = "ledger" }, 0, "minAvailable 5", 5, 5);

    private async Task AdvanceUntilAsync(Task run, TimeSpan limit)
    {
        for (var elapsed = TimeSpan.Zero; !run.IsCompleted && elapsed <= limit; elapsed += DrainNodeCommandHandler.PdbRetryInterval)
        {
            _time.Advance(DrainNodeCommandHandler.PdbRetryInterval);
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Five_pdb_blocked_pods_never_starve_an_evictable_pod()
    {
        Arrange([.. Enumerable.Range(1, 5).Select(Ledger), Api(1)], [LedgerPdb]);
        _ops.EvictPodAsync(Secret, "payments", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(K8sEvictionOutcome.BlockedByPdb);

        var run = Handler().HandleAsync(Command(), _progress);

        await WaitForAsync(() => EvictCalls("api-1") == 1); // no clock advance: well before the deadline
        _progress.Reports.Should().Contain(r => r.Last != null && r.Last.Name == "api-1" && r.Last.Outcome == DrainPodOutcome.Evicted);

        await AdvanceUntilAsync(run, TimeSpan.FromMinutes(6));
        var result = await run;

        result.Value!.TimedOut.Should().BeTrue();
        result.Value.Pods.Single(p => p.Name == "api-1").Outcome.Should().Be(DrainPodOutcome.Evicted);
        result.Value.Pods.Where(p => p.Namespace == "payments").Should().HaveCount(5).And.OnlyContain(p => p.Outcome == DrainPodOutcome.Blocked);
    }

    [Fact]
    public async Task A_blocked_pod_gives_up_its_slot_while_waiting_so_a_sixth_blocked_pod_is_tried()
    {
        Arrange(Enumerable.Range(1, 6).Select(Ledger).ToArray(), [LedgerPdb]);
        _ops.EvictPodAsync(Secret, "payments", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(K8sEvictionOutcome.BlockedByPdb);

        var run = Handler().HandleAsync(Command(), _progress);

        await WaitForAsync(() => EvictCalls() == 6);
        await AdvanceUntilAsync(run, TimeSpan.FromMinutes(6));
        (await run).Value!.Pods.Should().HaveCount(6).And.OnlyContain(p => p.Outcome == DrainPodOutcome.Blocked);
    }

    [Fact]
    public async Task Evictable_pods_are_attempted_before_pdb_blocked_ones()
    {
        Arrange([Ledger(1), Ledger(2), Api(1)], [LedgerPdb]);
        var order = new List<string>();
        _ops.EvictPodAsync(Secret, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lock (order)
                {
                    order.Add(ci.ArgAt<string>(2));
                }

                return K8sEvictionOutcome.Evicted;
            });

        await Handler().HandleAsync(Command(), _progress).WaitAsync(TimeSpan.FromSeconds(10));

        order.Should().HaveCount(3);
        order[0].Should().Be("api-1");
    }

    [Fact]
    public async Task A_forced_delete_is_not_sent_when_the_drain_was_stopped_while_the_eviction_was_in_flight()
    {
        Arrange([Ledger(1)], [LedgerPdb]);
        var gate = new TaskCompletionSource<K8sEvictionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ops.EvictPodAsync(Secret, "payments", "ledger-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(gate.Task);
        using var cts = new CancellationTokenSource();

        var run = Handler().HandleAsync(Command(force: true), _progress, cts.Token);
        await WaitForAsync(() => EvictCalls() == 1);
        await cts.CancelAsync();
        gate.SetResult(K8sEvictionOutcome.BlockedByPdb);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await _ops.DidNotReceiveWithAnyArgs().DeletePodAsync(default!, default!, default!, default, default);
        result.Value!.Cancelled.Should().BeTrue();
        result.Value.Pods.Single().Should().Be(
            new DrainPodResult("payments", "ledger-1", DrainPodOutcome.Blocked, "PodDisruptionBudget still blocking (stopped)"));
        AuditDetail().Should().Contain("blocked 1").And.Contain("deleted 0").And.Contain("cancelled after 1 of 1");
        AuditSucceeded().Should().BeFalse();
    }

    [Fact]
    public async Task An_ownerless_pod_under_a_blocking_pdb_is_never_evicted_without_force()
    {
        var labels = new Dictionary<string, string> { ["app"] = "debug" };
        var pdb = new K8sPdb("default", "debug", labels, 0, "maxUnavailable 0", 1, 1);
        Arrange([Pod("default", "debug-shell", null, null, labels)], [pdb]);
        _ops.EvictPodAsync(Secret, "default", "debug-shell", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(K8sEvictionOutcome.BlockedByPdb);

        var skipped = await Handler().HandleAsync(Command(), _progress).WaitAsync(TimeSpan.FromSeconds(10)); // a retry loop would never end

        skipped.Value!.Pods.Single().Should().Be(new DrainPodResult("default", "debug-shell", DrainPodOutcome.Skipped, "no controller — use force"));
        await _ops.DidNotReceiveWithAnyArgs().EvictPodAsync(default!, default!, default!, default, default);

        var forced = await Handler().HandleAsync(Command(force: true), _progress).WaitAsync(TimeSpan.FromSeconds(10));

        forced.Value!.Pods.Single().Outcome.Should().Be(DrainPodOutcome.Deleted);
        await _ops.Received(1).DeletePodAsync(Secret, "default", "debug-shell", 30, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_eviction_in_flight_when_stopped_completes_and_is_counted()
    {
        Arrange(Api(1), Api(2));
        var gate = new TaskCompletionSource<K8sEvictionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ops.EvictPodAsync(Secret, "checkout", "api-1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => gate.Task.WaitAsync(ci.ArgAt<CancellationToken>(4)));
        _ops.EvictPodAsync(Secret, "checkout", "api-2", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => gate.Task.WaitAsync(ci.ArgAt<CancellationToken>(4)));
        using var cts = new CancellationTokenSource();

        var run = Handler().HandleAsync(Command(), _progress, cts.Token);
        await WaitForAsync(() => EvictCalls() == 2);
        await cts.CancelAsync();
        await Task.Delay(20);
        gate.SetResult(K8sEvictionOutcome.Evicted);
        var result = await run;

        result.Value!.Cancelled.Should().BeTrue();
        result.Value.Pods.Should().HaveCount(2).And.OnlyContain(p => p.Outcome == DrainPodOutcome.Evicted);
        AuditDetail().Should().Be("grace 30s · force false · emptyDir false · evicted 2 · deleted 0 · blocked 0 · skipped 0 · cancelled after 2 of 2 · pods: checkout/api-1, checkout/api-2");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_grace_period_under_one_second_is_rejected_and_not_audited(int grace)
    {
        Arrange(Api(1));

        var result = await Handler().HandleAsync(Command(grace: grace), _progress);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Grace period must be at least 1 s.");
        await _ops.DidNotReceiveWithAnyArgs().SetUnschedulableAsync(default!, default!, default, default);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Pod_list_in_the_audit_detail_is_capped_at_fifty_names()
    {
        Arrange(Enumerable.Range(1, 55).Select(Api).ToArray());

        await Handler().HandleAsync(Command(), _progress);

        var detail = AuditDetail()!;
        detail.Should().EndWith(", …");
        detail[(detail.IndexOf("pods: ", StringComparison.Ordinal) + 6)..].Split(", ").Should().HaveCount(51);
    }

    [Fact]
    public async Task A_failed_cordon_is_audited_as_failed_and_nothing_is_evicted()
    {
        _ops.SetUnschedulableAsync(Secret, Node, true, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("boom"));

        var result = await Handler().HandleAsync(Command(), _progress);

        result.IsSuccess.Should().BeFalse();
        await _ops.DidNotReceiveWithAnyArgs().EvictPodAsync(default!, default!, default!, default, default);
        AuditSucceeded().Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_connection_is_not_audited()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(Command() with { Target = new K8sTarget(unknown, "x", null, Node) }, _progress);

        result.Error.Should().Be("Connection not found.");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Preview_plans_from_the_drain_inputs()
    {
        Arrange(Api(1), Pod("kube-system", "kube-proxy-1", "DaemonSet", "kube-proxy"));
        var preview = new PreviewDrainQueryHandler(_ops, _connections, NullLogger<PreviewDrainQueryHandler>.Instance);

        var result = await preview.HandleAsync(_id, Node, 30);

        result.Value!.WillEvict.Should().Be(1);
        result.Value.DaemonSets.Should().Be(1);
        result.Value.Estimate.Should().Be(TimeSpan.FromSeconds(30));
        await _ops.DidNotReceiveWithAnyArgs().SetUnschedulableAsync(default!, default!, default, default);
    }

    private sealed class RecordingProgress : IProgress<DrainProgress>
    {
        private readonly List<DrainProgress> _reports = [];

        public IReadOnlyList<DrainProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return _reports.ToList();
                }
            }
        }

        public void Report(DrainProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }
}
