using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Nodes;

namespace SbConsole.Plugins.Kubernetes.Tests.Nodes;

public class DrainPlannerTests
{
    private const string Node = "aks-pool1-38211-vmss00000a";

    internal static K8sPodSummary Pod(string ns, string name, string? ownerKind, string? ownerName,
        IReadOnlyDictionary<string, string>? labels = null, bool emptyDir = false, bool mirror = false) =>
        TestData.Pod(ns, name, node: Node) with
        {
            Owner = ownerKind is null ? null : new K8sRef(ownerKind, ownerKind == "Node" ? null : ns, ownerName!),
            ReplicaSet = null,
            Labels = labels ?? new Dictionary<string, string>(),
            HasEmptyDir = emptyDir,
            IsDaemonSetPod = ownerKind == "DaemonSet",
            IsMirrorPod = mirror,
        };

    internal static K8sDrainInputs Inputs(IReadOnlyList<K8sPodSummary> pods, IReadOnlyList<K8sPdb>? pdbs = null,
        IReadOnlyDictionary<string, int>? replicas = null) =>
        new(TestData.Node(Node), pods, pdbs ?? [], replicas ?? new Dictionary<string, int>());

    private static K8sPdb LedgerPdb(int allowed = 0) =>
        new("payments", "ledger", new Dictionary<string, string> { ["app"] = "ledger" }, allowed, "minAvailable 3", 3, 3);

    [Fact]
    public void Pdb_blocked_pods_come_first_with_the_pdb_note()
    {
        var plan = DrainPlanner.Plan(Inputs(
            [
                Pod("checkout", "cart-5f7b9-k2m8q", "Deployment", "cart"),
                Pod("payments", "ledger-1", "StatefulSet", "ledger", new Dictionary<string, string> { ["app"] = "ledger", ["tier"] = "db" }),
            ],
            [LedgerPdb()],
            new Dictionary<string, int> { ["Deployment/checkout/cart"] = 3, ["StatefulSet/payments/ledger"] = 3 }), 30);

        plan.Pods[0].Pod.Name.Should().Be("ledger-1");
        plan.Pods[0].Group.Should().Be(DrainGroup.BlockedByPdb);
        plan.Pods[0].Note.Should().Be("PDB ledger minAvailable 3 · now 3");
        plan.Pods[1].Group.Should().Be(DrainGroup.WillEvict);
        plan.Blocked.Should().Be(1);
        plan.WillEvict.Should().Be(1);
        plan.Node.Should().Be(Node);
    }

    [Fact]
    public void A_max_unavailable_pdb_note_has_no_current_count()
    {
        var pdb = new K8sPdb("platform", "kafka", new Dictionary<string, string> { ["app"] = "kafka" }, 0, "maxUnavailable 0", 3, 3);

        var plan = DrainPlanner.Plan(Inputs(
            [Pod("platform", "kafka-2", "StatefulSet", "kafka", new Dictionary<string, string> { ["app"] = "kafka" })], [pdb]), 30);

        plan.Pods.Single().Note.Should().Be("PDB kafka maxUnavailable 0");
    }

    [Fact]
    public void A_pdb_that_still_allows_disruptions_or_selects_other_labels_or_namespaces_does_not_block()
    {
        var labels = new Dictionary<string, string> { ["app"] = "ledger" };
        var plan = DrainPlanner.Plan(Inputs(
            [
                Pod("payments", "ledger-1", "StatefulSet", "ledger", labels),
                Pod("other", "ledger-1", "StatefulSet", "ledger", labels),
                Pod("payments", "api-1", "Deployment", "api", new Dictionary<string, string> { ["app"] = "api" }),
            ],
            [LedgerPdb(allowed: 1), LedgerPdb() with { Selector = new Dictionary<string, string> { ["app"] = "ledger", ["x"] = "y" } }]), 30);

        plan.Pods.Should().OnlyContain(p => p.Group == DrainGroup.WillEvict);
        plan.Blocked.Should().Be(0);
    }

    [Fact]
    public void DaemonSet_pods_are_ignored_and_last()
    {
        var plan = DrainPlanner.Plan(Inputs(
            [
                Pod("kube-system", "kube-proxy-4x8lq", "DaemonSet", "kube-proxy"),
                Pod("checkout", "cart-1", "Deployment", "cart"),
            ]), 30);

        plan.Pods.Select(p => p.Group).Should().Equal(DrainGroup.WillEvict, DrainGroup.DaemonSet);
        plan.Pods[1].Note.Should().Be("ignored");
        plan.DaemonSets.Should().Be(1);
        plan.WillEvict.Should().Be(1);
    }

    [Fact]
    public void Pods_without_a_controller_are_unmanaged_before_the_evictable_ones()
    {
        var plan = DrainPlanner.Plan(Inputs(
            [
                Pod("checkout", "cart-1", "Deployment", "cart"),
                Pod("default", "debug-shell", null, null),
            ]), 30);

        plan.Pods[0].Group.Should().Be(DrainGroup.Unmanaged);
        plan.Pods[0].Note.Should().Be("no controller");
        plan.WillEvict.Should().Be(1);
    }

    [Fact]
    public void Mirror_pods_are_excluded_entirely()
    {
        var plan = DrainPlanner.Plan(Inputs(
            [
                Pod("kube-system", "etcd-node-1", "Node", Node, mirror: true),
                Pod("checkout", "cart-1", "Deployment", "cart"),
            ]), 30);

        plan.Pods.Should().ContainSingle().Which.Pod.Name.Should().Be("cart-1");
    }

    [Fact]
    public void Replica_notes_say_how_many_of_the_owner_reschedule_or_warn_of_a_brief_outage()
    {
        var plan = DrainPlanner.Plan(Inputs(
            [
                Pod("checkout", "checkout-api-6c8d7-r9p2m", "Deployment", "checkout-api"),
                Pod("monitoring", "grafana-7c9d8-qq1zx", "Deployment", "grafana"),
                Pod("batch", "report-1", "Job", "report"),
            ],
            replicas: new Dictionary<string, int> { ["Deployment/checkout/checkout-api"] = 6, ["Deployment/monitoring/grafana"] = 1 }), 30);

        plan.Pods.Single(p => p.Pod.Name.StartsWith("checkout-api", StringComparison.Ordinal)).Note.Should().Be("1 of 6 reschedules");
        plan.Pods.Single(p => p.Pod.Name.StartsWith("grafana", StringComparison.Ordinal)).Note.Should().Be("brief outage");
        plan.Pods.Single(p => p.Pod.Name == "report-1").Note.Should().Be("reschedules");
    }

    [Fact]
    public void Every_replica_of_an_owner_on_this_node_is_a_brief_outage()
    {
        var plan = DrainPlanner.Plan(Inputs(
            [Pod("checkout", "cart-1", "Deployment", "cart"), Pod("checkout", "cart-2", "Deployment", "cart")],
            replicas: new Dictionary<string, int> { ["Deployment/checkout/cart"] = 2 }), 30);

        plan.Pods.Should().OnlyContain(p => p.Note == "brief outage");
    }

    [Fact]
    public void Within_a_group_pods_sort_by_namespace_then_name()
    {
        var plan = DrainPlanner.Plan(Inputs(
            [
                Pod("payments", "b", "Deployment", "x"), Pod("checkout", "z", "Deployment", "y"), Pod("payments", "a", "Deployment", "x"),
            ]), 30);

        plan.Pods.Select(p => $"{p.Pod.Namespace}/{p.Pod.Name}").Should().Equal("checkout/z", "payments/a", "payments/b");
    }

    [Fact]
    public void Plan_carries_the_emptyDir_flag()
    {
        var plan = DrainPlanner.Plan(Inputs([Pod("checkout", "cache-1", "Deployment", "cache", emptyDir: true)]), 30);

        plan.Pods.Single().HasEmptyDir.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 30, 0)]
    [InlineData(1, 30, 30)]
    [InlineData(5, 30, 30)]
    [InlineData(6, 30, 60)]
    [InlineData(31, 30, 210)]
    [InlineData(11, 10, 30)]
    public void Estimate_is_batches_of_five_times_the_grace_period(int pods, int grace, int expectedSeconds)
    {
        var plan = DrainPlanner.Plan(Inputs(
            Enumerable.Range(0, pods).Select(i => Pod("checkout", $"api-{i}", "Deployment", "api")).ToList()), grace);

        plan.Estimate.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }
}
