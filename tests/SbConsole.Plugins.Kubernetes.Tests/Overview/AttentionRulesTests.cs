using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Overview;
using static SbConsole.Plugins.Kubernetes.Tests.TestData;

namespace SbConsole.Plugins.Kubernetes.Tests.Overview;

public class AttentionRulesTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Func<string, string> Plain = h => h;

    private static IReadOnlyList<AttentionItem> Build(K8sOverview o) => AttentionRules.Build(o, T0, Plain);

    [Fact]
    public void Healthy_overview_needs_no_attention() =>
        Build(OverviewWith()).Should().BeEmpty();

    [Fact]
    public void Crash_loops_come_first_ordered_by_restarts()
    {
        var o = OverviewWith(problemPods:
        [
            Pod(name: "few", status: "CrashLoopBackOff", restarts: 3),
            Pod(name: "many", status: "CrashLoopBackOff", restarts: 40, lastReason: "OOMKilled", lastExit: 137),
        ]);

        var items = Build(o);

        items.Select(i => i.Title).Should().Equal("many", "few");
        items[0].Should().Be(new AttentionItem("crash loop", "Error", "checkout", "many",
            "40 restarts · last exit OOMKilled (137)", "Pod", KubernetesPlugin.PodHref("checkout", "many")));
        items[1].Detail.Should().Be("3 restarts");
    }

    [Fact]
    public void Not_ready_nodes_show_age_and_pods_affected()
    {
        var o = OverviewWith(nodes: [Node("ok"), Node("bad", ready: "False", readySince: T0.AddMinutes(-12), pods: 14), Node("lost", ready: "Unknown", readySince: null, pods: 1)]);
        o = o with { Nodes = [o.Nodes[0], o.Nodes[1], o.Nodes[2] with { ReadySince = null }] };

        var items = Build(o);

        items.Should().Equal(
            new AttentionItem("not ready", "Error", null, "bad", "NotReady for 12m · 14 pods affected", "Node", KubernetesPlugin.NodesHref),
            new AttentionItem("not ready", "Error", null, "lost", "Unknown · 1 pod affected", "Node", KubernetesPlugin.NodesHref));
    }

    [Fact]
    public void Unavailable_deployments_and_failed_jobs()
    {
        var o = OverviewWith(problemWorkloads:
        [
            Job(name: "migrate"),
            Deployment(name: "web", desired: 3, available: 1),
            Deployment(name: "stuck", desired: 2, available: 2,
                conditions: [new K8sCondition("Progressing", "False", "ProgressDeadlineExceeded", "ReplicaSet has timed out", null)]),
        ]);

        var items = Build(o);

        items.Should().Equal(
            new AttentionItem("unavailable", "Error", "checkout", "web", "1/3 available", "Deployment", KubernetesPlugin.DeploymentHref("checkout", "web")),
            new AttentionItem("unavailable", "Error", "checkout", "stuck", "2/2 available · ProgressDeadlineExceeded", "Deployment", KubernetesPlugin.DeploymentHref("checkout", "stuck")),
            new AttentionItem("job failed", "Error", "checkout", "migrate", "BackoffLimitExceeded", "Jobs", KubernetesPlugin.WorkloadsHref + "?tab=jobs"));
    }

    [Fact]
    public void Unschedulable_pods_are_grouped_per_namespace_with_the_first_message_truncated()
    {
        var longMessage = "0/3 nodes are available: " + new string('x', 200);
        var o = OverviewWith(problemPods:
        [
            Pod(ns: "payments", name: "p1", phase: "Pending", unschedulable: longMessage),
            Pod(ns: "checkout", name: "c1", phase: "Pending", unschedulable: "0/1 nodes are available: 1 Insufficient memory."),
            Pod(ns: "payments", name: "p2", phase: "Pending", unschedulable: "other"),
        ]);

        var items = Build(o);

        items.Should().HaveCount(2);
        items[0].Should().Be(new AttentionItem("pending", "Warning", "checkout", "1 pod unschedulable",
            "0/1 nodes are available: 1 Insufficient memory.", "Events", KubernetesPlugin.EventsHref));
        items[1].Title.Should().Be("2 pods unschedulable");
        items[1].Namespace.Should().Be("payments");
        items[1].Detail.Should().HaveLength(120).And.StartWith("0/3 nodes are available: ").And.EndWith("…");
    }

    [Fact]
    public void Groups_are_ordered_crash_loops_nodes_deployments_jobs_unschedulable()
    {
        var o = OverviewWith(
            nodes: [Node("bad", ready: "False")],
            problemPods:
            [
                Pod(name: "pending", phase: "Pending", unschedulable: "no room"),
                Pod(name: "crash", status: "CrashLoopBackOff", restarts: 5),
                Pod(name: "failed", phase: "Failed"),
            ],
            problemWorkloads: [Job(), Deployment(name: "web", available: 0)]);

        Build(o).Select(i => i.Chip).Should().Equal("crash loop", "not ready", "unavailable", "job failed", "pending");
    }

    [Fact]
    public void Build_is_capped_and_build_all_is_not()
    {
        var pods = Enumerable.Range(0, 14).Select(i => Pod(name: $"p{i}", status: "CrashLoopBackOff", restarts: 100 - i)).ToList();
        var o = OverviewWith(problemPods: pods);

        AttentionRules.Cap.Should().Be(10);
        Build(o).Should().HaveCount(10);
        Build(o)[^1].Title.Should().Be("p9");
        AttentionRules.BuildAll(o, T0, Plain).Should().HaveCount(14);
    }

    [Fact]
    public void Links_include_the_connection_id()
    {
        var o = OverviewWith(
            nodes: [Node("bad", ready: "False")],
            problemPods: [Pod(name: "crash", status: "CrashLoopBackOff", restarts: 5)],
            problemWorkloads: [Job()]);

        var items = AttentionRules.Build(o, T0, AttentionRules.WithConnection(Id));

        items.Select(i => i.Href).Should().Equal(
            $"/p/kubernetes/pods/checkout/crash?connectionId={Id}",
            $"/p/kubernetes/nodes?connectionId={Id}",
            $"/p/kubernetes/workloads?tab=jobs&connectionId={Id}");
    }
}
