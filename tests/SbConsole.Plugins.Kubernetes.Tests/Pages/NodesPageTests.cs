using System.Net;
using Bunit;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Plugins.Kubernetes.Overview;
using SbConsole.Sdk;
using NodesPage = SbConsole.Plugins.Kubernetes.Pages.Nodes;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class NodesPageTests : K8sPageTestBase
{
    private const string NotReady = "aks-pool1-38211-vmss00000a";
    private const string Pressured = "aks-pool1-38211-vmss000004";
    private const string Healthy = "aks-pool1-38211-vmss000001";

    public NodesPageTests() => ArrangeNodes(TestData.Nodes());

    private void ArrangeNodes(IReadOnlyList<K8sNodeInfo> nodes) =>
        Operations.ListNodesAsync(Secret, Arg.Any<CancellationToken>()).Returns(_ => nodes);

    private int ListCalls() =>
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ListNodesAsync));

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage()
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<NodesPage>(2);
            builder.CloseComponent();
        };

        var cut = Render(fragment);
        cut.WaitForState(() => cut.FindAll(".k8s-nodes-table, .k8s-nodes-error").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static IReadOnlyList<string> Names(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindAll(".k8s-node-row").Select(r => r.QuerySelector(".k8s-node-name")!.TextContent.Trim()).ToList();

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".k8s-node-row").Single(r => r.QuerySelector(".k8s-node-name")!.TextContent.Trim() == name);

    private static void OpenMenu(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name)
    {
        Row(cut, name).QuerySelector(".k8s-node-menu button")!.Click();
        cut.WaitForAssertion(() => cut.FindAll(".mud-menu-item").Should().NotBeEmpty());
    }

    private static void ClickMenuItem(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string text) =>
        cut.FindAll(".mud-menu-item").First(i => i.TextContent.Contains(text)).Click();

    [Fact]
    public void Header_summarises_nodes_pools_ready_and_cordoned()
    {
        var cut = RenderPage();

        cut.Find(".k8s-header .k8s-title").TextContent.Should().Be("Nodes");
        cut.Find(".k8s-nodes-summary").TextContent.Trim().Should().Be("12 nodes · 2 pools · 11 ready · 1 cordoned");
        cut.FindAll(".k8s-ns-picker").Should().BeEmpty("nodes are cluster-scoped");
    }

    [Fact]
    public void Not_ready_node_sorts_first_and_its_bars_are_dimmed()
    {
        var cut = RenderPage();

        Names(cut)[0].Should().Be(NotReady);
        Names(cut).Skip(1).Should().BeInAscendingOrder(StringComparer.Ordinal);
        var row = Row(cut, NotReady);
        row.ClassList.Should().Contain("k8s-node-notready");
        row.QuerySelectorAll(".k8s-node-bar").Should().HaveCount(2).And.OnlyContain(b => b.ClassList.Contains("k8s-stale"));
        row.QuerySelector(".k8s-node-ready")!.TextContent.Trim().Should().Be("NotReady");
        row.QuerySelector(".k8s-node-ready-age")!.TextContent.Trim().Should().Be("Unknown 6m");

        var healthy = Row(cut, Healthy);
        healthy.ClassList.Should().NotContain("k8s-node-notready");
        healthy.QuerySelectorAll(".k8s-node-bar").Should().HaveCount(2).And.OnlyContain(b => !b.ClassList.Contains("k8s-stale"));
        healthy.QuerySelector(".k8s-node-ready")!.TextContent.Trim().Should().Be("Ready");
        healthy.QuerySelector(".k8s-node-ready-age").Should().BeNull();
    }

    [Fact]
    public void Row_shows_instance_ip_roles_taints_kubelet_requests_pods_and_age()
    {
        var cut = RenderPage();

        var row = Row(cut, NotReady);
        row.QuerySelector(".k8s-node-caption")!.TextContent.Trim().Should().Be("Standard_D8s_v5 · 10.240.0.13");
        row.QuerySelector(".k8s-node-roles")!.TextContent.Trim().Should().Be("agent");
        row.QuerySelector(".k8s-node-kubelet")!.TextContent.Trim().Should().Be("v1.30.4");
        row.QuerySelector(".k8s-node-cpu")!.TextContent.Trim().Should().Be("4.6 / 7.9");
        row.QuerySelector(".k8s-node-mem")!.TextContent.Trim().Should().Be("21 Gi / 28 Gi");
        row.QuerySelector(".k8s-node-pods")!.TextContent.Trim().Should().Be("34");
        row.QuerySelector(".k8s-node-age")!.TextContent.Trim().Should().Be("38d");
        Row(cut, "aks-pool2-40917-vmss000000").QuerySelector(".k8s-node-roles")!.TextContent.Trim()
            .Should().Be("agent · workload=memory:NoSchedule");
    }

    [Fact]
    public void Cpu_reads_in_cores_even_below_one_core()
    {
        ArrangeNodes([TestData.Nodes()[0] with { CpuRequestedMilli = 300, CpuAllocatableMilli = 4000 }]);
        var cut = RenderPage();

        cut.Find(".k8s-node-cpu").TextContent.Trim().Should().Be("0.3 / 4");
    }

    [Fact]
    public void Pressure_chips_show_only_when_true()
    {
        var cut = RenderPage();

        Row(cut, Pressured).QuerySelectorAll(".k8s-pressure").Select(c => c.TextContent.Trim()).Should().Equal("MemoryPressure");
        cut.FindAll(".k8s-pressure").Should().HaveCount(1);
    }

    [Fact]
    public void Cordoned_row_shows_the_chip_and_uncordon_others_show_yes_and_cordon()
    {
        var cut = RenderPage();

        var cordoned = Row(cut, NotReady);
        cordoned.QuerySelector(".k8s-cordoned")!.TextContent.Trim().Should().Be("cordoned");
        cordoned.QuerySelector(".k8s-uncordon")!.TextContent.Trim().Should().Be("Uncordon");
        cordoned.QuerySelector(".k8s-cordon").Should().BeNull();

        var healthy = Row(cut, Healthy);
        healthy.QuerySelector(".k8s-node-schedulable")!.TextContent.Trim().Should().Be("yes");
        healthy.QuerySelector(".k8s-cordon")!.TextContent.Trim().Should().Be("Cordon");
        healthy.QuerySelector(".k8s-uncordon").Should().BeNull();
    }

    [Fact]
    public async Task Cordon_asks_a_plain_confirmation_then_cordons_and_reloads()
    {
        Confirmation.ConfirmAsync("Cordon", Healthy, false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();
        var lists = ListCalls();

        Row(cut, Healthy).QuerySelector(".k8s-cordon")!.Click();

        cut.WaitForAssertion(() => Operations.Received(1).SetUnschedulableAsync(Secret, Healthy, true, Arg.Any<CancellationToken>()));
        await Confirmation.Received(1).ConfirmAsync("Cordon", Healthy, false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("k8s.node.cordon", $"{ConnectionName}/{Healthy}", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => ListCalls().Should().BeGreaterThan(lists));
    }

    [Fact]
    public async Task Uncordon_asks_a_plain_confirmation_then_uncordons()
    {
        Confirmation.ConfirmAsync("Uncordon", NotReady, false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        Row(cut, NotReady).QuerySelector(".k8s-uncordon")!.Click();

        cut.WaitForAssertion(() => Operations.Received(1).SetUnschedulableAsync(Secret, NotReady, false, Arg.Any<CancellationToken>()));
        await Audit.Received(1).RecordAsync("k8s.node.uncordon", $"{ConnectionName}/{NotReady}", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declined_cordon_does_nothing()
    {
        Confirmation.ConfirmAsync("Cordon", Healthy, false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        Row(cut, Healthy).QuerySelector(".k8s-cordon")!.Click();

        await Confirmation.Received(1).ConfirmAsync("Cordon", Healthy, false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().SetUnschedulableAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Pool_filter_shows_only_that_pools_nodes()
    {
        var cut = RenderPage();
        var pool = cut.FindComponents<MudSelect<string>>().Single(s => s.Instance.Class!.Contains("k8s-pool-filter"));

        await cut.InvokeAsync(() => pool.Instance.ValueChanged.InvokeAsync("pool2"));

        cut.WaitForAssertion(() => Names(cut).Should().HaveCount(7).And.OnlyContain(n => n.StartsWith("aks-pool2-", StringComparison.Ordinal)));
        cut.Find(".k8s-nodes-summary").TextContent.Trim().Should().Be("12 nodes · 2 pools · 11 ready · 1 cordoned");

        await cut.InvokeAsync(() => pool.Instance.ValueChanged.InvokeAsync(""));
        cut.WaitForAssertion(() => Names(cut).Should().HaveCount(12));
    }

    [Fact]
    public void Viewer_sees_no_cordon_or_uncordon_and_the_rbac_chip()
    {
        Operations.GetAccessAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.ViewerAccess("sbconsole-viewer"));
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-rbac-chip").Should().HaveCount(1));
        cut.FindAll(".k8s-cordon").Should().BeEmpty();
        cut.FindAll(".k8s-uncordon").Should().BeEmpty();
        OpenMenu(cut, Healthy);
        cut.FindAll(".mud-menu-item").Select(i => i.TextContent.Trim()).Should().NotContain(t => t.Contains("Drain"));
    }

    [Fact]
    public void Menu_offers_yaml_pods_and_kubectl_and_ends_there()
    {
        var cut = RenderPage();

        OpenMenu(cut, NotReady);

        cut.FindAll(".mud-menu-item").Select(i => i.TextContent.Trim()).Should()
            .Equal("View YAML", "Show 34 pods on this node", "Copy kubectl command");
    }

    [Fact]
    public void Show_pods_dialog_lists_the_nodes_pods_with_links()
    {
        Operations.ListPodsOnNodeAsync(Secret, NotReady, Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<K8sPodSummary>)
        [
            TestData.Pod("checkout", "payment-gateway-7d9f8-x2k4q", status: "CrashLoopBackOff", restarts: 14, node: NotReady),
            TestData.Pod("platform", "log-shipper-mq8zl", node: NotReady),
        ]);
        var cut = RenderPage();

        OpenMenu(cut, NotReady);
        ClickMenuItem(cut, "Show 34 pods on this node");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-node-pod-row").Should().HaveCount(2));
        var first = cut.FindAll(".k8s-node-pod-row")[0];
        first.TextContent.Should().Contain("checkout").And.Contain("CrashLoopBackOff").And.Contain("14").And.Contain("1h");
        first.QuerySelector("[href]")!.GetAttribute("href").Should()
            .Be(AttentionRules.WithConnection(ConnectionId)(KubernetesPlugin.PodHref("checkout", "payment-gateway-7d9f8-x2k4q")));
    }

    [Fact]
    public void View_yaml_reads_the_cluster_scoped_node()
    {
        Operations.GetYamlAsync(Secret, K8sResourceKind.Node, null, NotReady, Arg.Any<CancellationToken>()).Returns("kind: Node\n");
        var cut = RenderPage();

        OpenMenu(cut, NotReady);
        ClickMenuItem(cut, "View YAML");

        cut.WaitForAssertion(() => cut.Find("pre.k8s-yaml").TextContent.Should().Contain("kind: Node"));
    }

    [Fact]
    public void Copy_kubectl_command_copies_describe_node()
    {
        var module = JSInterop.SetupModule(K8sJs.ModulePath);
        module.SetupVoid("copyText", $"kubectl describe node {NotReady}");
        var cut = RenderPage();

        OpenMenu(cut, NotReady);
        ClickMenuItem(cut, "Copy kubectl command");

        cut.WaitForAssertion(() => module.VerifyInvoke("copyText").Arguments.Should().Equal($"kubectl describe node {NotReady}"));
    }

    [Fact]
    public void Forbidden_node_list_shows_a_friendly_error()
    {
        Operations.ListNodesAsync(Secret, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<K8sNodeInfo>>(_ => throw new HttpOperationException("raw")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden),
                "{\"kind\":\"Status\",\"message\":\"nodes is forbidden: User \\\"x\\\" cannot list resource \\\"nodes\\\" in API group \\\"\\\" at the cluster scope\"}"),
        });
        var cut = RenderPage();

        cut.Find(".k8s-nodes-error").TextContent.Should().Contain("Can't list nodes.").And.Contain("Your account can't list nodes.");
        cut.FindAll(".k8s-node-row").Should().BeEmpty();
    }

    [Fact]
    public void Fifty_rows_then_show_all()
    {
        var template = TestData.Nodes()[0];
        ArrangeNodes(Enumerable.Range(0, 55).Select(i => template with { Name = $"node-{i:d3}" }).ToList());
        var cut = RenderPage();

        cut.FindAll(".k8s-node-row").Should().HaveCount(50);
        cut.Find(".k8s-nodes-more").TextContent.Should().Contain("5 more nodes · show all");

        cut.Find(".k8s-nodes-show-all").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-node-row").Should().HaveCount(55));
        cut.FindAll(".k8s-nodes-more").Should().BeEmpty();
    }

    [Fact]
    public void Poller_reloads_every_ten_seconds()
    {
        var cut = RenderPage();
        var lists = ListCalls();

        Time.Advance(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() => ListCalls().Should().Be(lists + 1));
    }

    [Fact]
    public void Footnote_explains_cordon_and_drain_risk()
    {
        var cut = RenderPage();

        cut.Find(".k8s-nodes-footnote").TextContent.Should()
            .Contain("Cordon/Uncordon flip one field and are Mutating. Drain evicts every pod and is Destructive — it previews the eviction list first.")
            .And.Contain("Audited as k8s.node.cordon / .drain");
    }
}
