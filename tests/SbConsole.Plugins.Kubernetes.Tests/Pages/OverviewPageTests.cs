using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Access;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Plugins.Kubernetes.Overview;
using SbConsole.Sdk;
using OverviewPage = SbConsole.Plugins.Kubernetes.Pages.Overview;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class OverviewPageTests : K8sPageTestBase
{
    private static readonly DateTimeOffset T0 = TestData.T0;

    private void Arrange(K8sOverview overview) =>
        Operations.GetOverviewAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(overview);

    private void ArrangeFailure() =>
        Operations.GetOverviewAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<K8sOverview>(_ => throw new HttpRequestException("No such host is known. (aks-prod-eu.example:443)"));

    private int OverviewCalls() =>
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.GetOverviewAsync));

    private IRenderedComponent<OverviewPage> RenderPage()
    {
        var cut = Render<OverviewPage>();
        cut.WaitForState(() => cut.FindAll(".k8s-overview-body").Count > 0 && cut.FindAll(".k8s-overview-skeleton").Count == 0,
            TimeSpan.FromSeconds(5));
        return cut;
    }

    private string Link(string href) => AttentionRules.WithConnection(ConnectionId)(href);

    [Fact]
    public void Cluster_header_shows_provider_server_context_versions_and_identity()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();

        var header = cut.Find(".k8s-cluster-header");
        cut.Find(".k8s-provider-chip").TextContent.Trim().Should().Be("AKS");
        header.TextContent.Should()
            .Contain("https://aks-prod-eu-dns-8f2a1c.hcp.westeurope.azmk8s.io:443 · context aks-prod-eu-admin")
            .And.Contain("server v1.30.4 · 12 nodes in 2 pools · 14 namespaces")
            .And.Contain("connected as sbconsole-admin");
    }

    [Fact]
    public void Cluster_header_omits_pools_when_no_node_has_one()
    {
        var overview = TestData.Overview();
        Arrange(overview with { Nodes = overview.Nodes.Select(n => n with { Pool = null }).ToList() });
        var cut = RenderPage();

        cut.Find(".k8s-cluster-header").TextContent.Should().Contain("server v1.30.4 · 12 nodes · 14 namespaces");
    }

    [Fact]
    public void Tiles_render_values_and_captions_from_the_overview()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();

        var nodes = cut.Find(".k8s-tile-nodes");
        nodes.TextContent.Should().Contain("Nodes ready").And.Contain("11").And.Contain("/ 12").And.Contain("aks-pool1-…0000a NotReady 6m");
        nodes.ClassList.Should().Contain("k8s-warning");
        cut.Find(".k8s-tile-pods").TextContent.Should().Contain("412").And.Contain("running")
            .And.Contain("6 pending · 3 failed · 41 succeeded");
        cut.Find(".k8s-tile-crashloop .k8s-tile-number").TextContent.Should().Be("2");
        cut.Find(".k8s-tile-crashloop").TextContent.Should().Contain("checkout/payment-gateway · platform/log-shipper");
        cut.Find(".k8s-tile-cpu .k8s-tile-number").TextContent.Should().Be("61%");
        cut.Find(".k8s-tile-cpu").TextContent.Should().Contain("58.6 / 96 cores · limits 142 · usage 38%");
        cut.Find(".k8s-tile-memory .k8s-tile-number").TextContent.Should().Be("78%");
        cut.Find(".k8s-tile-memory").TextContent.Should().Contain("299 Gi / 384 Gi · 1 node with > 8 Gi free");
        cut.FindAll(".k8s-tile-cpu .k8s-tile-meter").Should().HaveCount(1);
        cut.FindAll(".k8s-tile-memory .k8s-tile-meter").Should().HaveCount(1);
    }

    [Fact]
    public void Crash_loop_tile_has_the_error_outline_and_links_to_the_worst_pod()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();

        var tile = cut.Find(".k8s-tile-crashloop");
        tile.ClassList.Should().Contain("k8s-error-outline").And.Contain("k8s-warning");
        var link = tile.QuerySelector("a")!;
        link.TextContent.Should().Be("Open pod →");
        link.GetAttribute("href").Should().Be(Link(KubernetesPlugin.PodHref("checkout", "payment-gateway-7d9f8-x2k4q")));
    }

    [Fact]
    public void A_healthy_cluster_has_calm_tiles_and_a_friendly_attention_state()
    {
        Arrange(TestData.OverviewWith());
        var cut = RenderPage();

        var nodes = cut.Find(".k8s-tile-nodes");
        nodes.TextContent.Should().Contain("all ready");
        nodes.ClassList.Should().NotContain("k8s-warning");
        var crash = cut.Find(".k8s-tile-crashloop");
        crash.ClassList.Should().NotContain("k8s-error-outline");
        crash.QuerySelector("a").Should().BeNull();
        cut.Find(".k8s-tile-cpu").TextContent.Should().NotContain("usage");
        cut.Find(".k8s-attention-empty").TextContent.Should().Be("Nothing needs attention.");
        cut.FindAll(".k8s-attention-item").Should().BeEmpty();
    }

    [Fact]
    public void Forbidden_nodes_show_dashes_and_not_permitted_instead_of_all_ready()
    {
        Arrange(TestData.OverviewWith(nodes: []) with { NodesForbidden = true });
        var cut = RenderPage();

        var nodes = cut.Find(".k8s-tile-nodes");
        nodes.QuerySelector(".k8s-tile-number")!.TextContent.Should().Be("—");
        nodes.TextContent.Should().Contain("not permitted for this account").And.NotContain("all ready").And.NotContain("/ 0");
        nodes.ClassList.Should().NotContain("k8s-warning");
        cut.FindAll(".k8s-tile-nodes .k8s-tile-meter").Should().BeEmpty();
        cut.Find(".k8s-tile-cpu .k8s-tile-number").TextContent.Should().Be("—");
        cut.Find(".k8s-tile-cpu").TextContent.Should().Contain("not permitted").And.NotContain("cores");
        cut.Find(".k8s-tile-memory .k8s-tile-number").TextContent.Should().Be("—");
        cut.Find(".k8s-tile-memory").TextContent.Should().Contain("not permitted").And.NotContain("free");
        cut.Find(".k8s-cluster-header").TextContent.Should().NotContain("0 nodes");
    }

    [Fact]
    public void Attention_items_are_worst_first_with_links_carrying_the_connection_id()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();

        cut.Find(".k8s-attention .k8s-attention-count").TextContent.Trim().Should().Be("6");
        cut.Find(".k8s-attention").TextContent.Should().Contain("worst first");
        var items = cut.FindAll(".k8s-attention-item");
        items.Select(i => i.QuerySelector(".k8s-attention-chip")!.TextContent.Trim())
            .Should().Equal("crash loop", "crash loop", "not ready", "unavailable", "job failed", "pending");
        items[0].TextContent.Should().Contain("checkout").And.Contain("payment-gateway-7d9f8-x2k4q")
            .And.Contain("14 restarts · last exit OOMKilled (137)");
        items[0].QuerySelector(".k8s-attention-chip")!.ClassList.Should().Contain("mud-chip-color-error");
        items[5].QuerySelector(".k8s-attention-chip")!.ClassList.Should().Contain("mud-chip-color-warning");
        items[5].TextContent.Should().Contain("monitoring").And.Contain("6 pods unschedulable");
        items.Select(i => i.QuerySelector("a")!.TextContent.Trim())
            .Should().Equal("Pod →", "Pod →", "Node →", "Deployment →", "Jobs →", "Events →");
        items.Select(i => i.QuerySelector("a")!.GetAttribute("href")).Should().Equal(
            Link(KubernetesPlugin.PodHref("checkout", "payment-gateway-7d9f8-x2k4q")),
            Link(KubernetesPlugin.PodHref("platform", "log-shipper-mq8zl")),
            Link(KubernetesPlugin.NodesHref),
            Link(KubernetesPlugin.DeploymentHref("checkout", "payment-gateway")),
            Link(AttentionRules.JobsHref),
            Link(KubernetesPlugin.EventsHref));
    }

    [Fact]
    public void Recent_warning_events_show_the_first_six_rows()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();

        var card = cut.Find(".k8s-recent-events");
        card.TextContent.Should().Contain("Recent Warning events").And.Contain("last 1h · 38");
        card.QuerySelector(".k8s-events-all")!.GetAttribute("href").Should().Be(Link(KubernetesPlugin.EventsHref));
        var rows = cut.FindAll(".k8s-event-row");
        rows.Should().HaveCount(6);
        rows[0].QuerySelector(".k8s-event-age")!.TextContent.Trim().Should().Be("2m");
        rows[0].TextContent.Should().Contain("BackOff").And.Contain("Pod").And.Contain("payment-gateway-7d9f8-x2k4q")
            .And.Contain("Back-off restarting failed container gateway");
        rows[0].QuerySelector(".k8s-event-count")!.TextContent.Trim().Should().Be("×14");
        rows[2].TextContent.Should().Contain("NodeNotReady").And.Contain("Node").And.Contain("aks-pool1-38211-vmss00000a");
        rows[5].QuerySelector(".k8s-event-age")!.TextContent.Trim().Should().Be("41m");
        cut.Find(".k8s-overview-footnote").TextContent.Should().Be("Metrics are reads and are not audited.");
    }

    [Fact]
    public void Header_shows_refreshed_time_and_the_live_indicator()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();

        cut.Find(".k8s-overview-refreshed").TextContent.Should().Be("refreshed just now");
        cut.Find(".k8s-live").TextContent.Trim().Should().Be("watch · live");
    }

    [Fact]
    public async Task Unreachable_with_a_snapshot_dims_tiles_as_stale_and_shows_the_error()
    {
        await OverviewSnapshotStore.WriteGoodAsync(Store, ConnectionId, new OverviewSnapshot(TestData.Overview(), T0.AddMinutes(-2), []), default);
        ArrangeFailure();
        var cut = RenderPage();

        cut.Find(".k8s-overview-error").TextContent.Should().Contain("Can't reach aks-prod-eu.");
        cut.Find(".k8s-overview-retry-in").TextContent.Should().Be("retrying in 30 s");
        cut.Find(".k8s-overview-edit").GetAttribute("href").Should().Be("/connections");
        var tile = cut.Find(".k8s-tile-nodes");
        tile.ClassList.Should().Contain("k8s-stale");
        tile.TextContent.Should().Contain("stale · 11:58");
        cut.Find(".k8s-overview-last-ok").TextContent.Should().Be("last ok 11:58");
        cut.FindAll(".k8s-overview-refreshed").Should().BeEmpty();
        cut.Find(".k8s-attention").TextContent.Should().Contain("waiting for connection");
        cut.FindAll(".k8s-attention .mud-skeleton").Should().NotBeEmpty();
        cut.FindAll(".k8s-recent-events .mud-skeleton").Should().NotBeEmpty();
    }

    [Fact]
    public void Unreachable_without_a_snapshot_shows_skeleton_tiles_and_the_error()
    {
        ArrangeFailure();
        var cut = RenderPage();

        cut.FindAll(".k8s-overview-error").Should().HaveCount(1);
        cut.FindAll(".k8s-tiles .k8s-tile").Should().HaveCount(5);
        cut.FindAll(".k8s-tiles .mud-skeleton").Should().NotBeEmpty();
        cut.FindAll(".k8s-tile-nodes").Should().BeEmpty();
        cut.FindAll(".k8s-cluster-header").Should().BeEmpty();
    }

    [Fact]
    public void Retry_now_reloads_and_recovery_clears_the_error()
    {
        ArrangeFailure();
        var cut = RenderPage();
        OverviewCalls().Should().Be(1);

        Arrange(TestData.Overview());
        cut.Find(".k8s-overview-retry").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-overview-error").Should().BeEmpty());
        OverviewCalls().Should().Be(2);
        cut.FindAll(".k8s-tile-nodes").Should().HaveCount(1);
    }

    [Fact]
    public void Countdown_ticks_each_second_and_reloads_at_zero()
    {
        ArrangeFailure();
        var cut = RenderPage();

        Time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.Find(".k8s-overview-retry-in").TextContent.Should().Be("retrying in 25 s"));
        // The 5 s poller stays quiet while the error countdown owns retries.
        OverviewCalls().Should().Be(1);

        Arrange(TestData.Overview());
        Time.Advance(TimeSpan.FromSeconds(25));
        cut.WaitForAssertion(() => OverviewCalls().Should().Be(2));
        cut.WaitForAssertion(() => cut.FindAll(".k8s-overview-error").Should().BeEmpty());
    }

    [Fact]
    public async Task A_missing_connection_result_does_not_auto_retry()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        var cut = RenderPage();

        cut.FindAll(".k8s-overview-error").Should().HaveCount(1);
        cut.FindAll(".k8s-overview-retry-in").Should().BeEmpty();
        Time.Advance(TimeSpan.FromSeconds(90));
        await Task.Delay(100);
        OverviewCalls().Should().Be(0);
    }

    [Fact]
    public void Poller_reloads_after_five_seconds()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();
        OverviewCalls().Should().Be(1);

        Arrange(TestData.Overview() with { Pods = new K8sPodCounts(500, 0, 0, 0) });
        Time.Advance(TimeSpan.FromSeconds(5));

        cut.WaitForAssertion(() => cut.Find(".k8s-tile-pods").TextContent.Should().Contain("500"));
        OverviewCalls().Should().Be(2);
    }

    [Fact]
    public async Task Poller_pauses_while_the_tab_is_hidden()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();

        await cut.InvokeAsync(() => cut.Instance.SetHidden(true));
        Time.Advance(TimeSpan.FromSeconds(15));
        await Task.Delay(100);
        OverviewCalls().Should().Be(1);
        cut.Find(".k8s-live").TextContent.Trim().Should().Be("watch · paused");

        await cut.InvokeAsync(() => cut.Instance.SetHidden(false));
        Time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => OverviewCalls().Should().Be(2));
    }

    [Fact]
    public async Task Namespace_change_reloads_with_that_namespace_list()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();
        await Operations.Received(1).GetOverviewAsync(Secret, Arg.Is<IReadOnlyList<string>>(n => n.Count == 0), Arg.Any<CancellationToken>());

        var picker = cut.FindComponent<K8sNamespacePicker>();
        await cut.InvokeAsync(() => picker.Instance.SelectionChanged.InvokeAsync(["checkout", "payments"]));

        cut.WaitForAssertion(() => Operations.Received(1).GetOverviewAsync(Secret,
            Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "checkout", "payments" })), Arg.Any<CancellationToken>()));

        // The poller keeps running and uses the new selection.
        Time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Operations.Received(2).GetOverviewAsync(Secret,
            Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "checkout", "payments" })), Arg.Any<CancellationToken>()));
    }

    [Fact]
    public async Task Connection_change_clears_old_data_before_the_new_load_finishes()
    {
        var secondId = Guid.NewGuid();
        const string secondSecret = "second-secret";
        var connections = await Connections.ListAsync(KubernetesPlugin.Kind);
        var second = connections[0] with { Id = secondId, Name = "aks-dev" };
        Connections.ListAsync(KubernetesPlugin.Kind, Arg.Any<CancellationToken>()).Returns([connections[0], second]);
        Connections.GetSecretAsync(secondId, Arg.Any<CancellationToken>()).Returns(secondSecret);
        var gate = new TaskCompletionSource<K8sOverview>();
        Operations.GetOverviewAsync(secondSecret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        Arrange(TestData.Overview());
        var cut = RenderPage();
        cut.FindAll(".k8s-tile-nodes").Should().HaveCount(1);

        var picker = cut.FindComponent<K8sConnectionPicker>();
        await cut.InvokeAsync(() => picker.Instance.SelectionChanged.InvokeAsync(second));

        cut.WaitForAssertion(() => Operations.Received(1).GetOverviewAsync(secondSecret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()));
        cut.FindAll(".k8s-tile-nodes").Should().BeEmpty();
        cut.FindAll(".k8s-cluster-header").Should().BeEmpty();
        cut.FindAll(".k8s-overview-skeleton").Should().HaveCount(1);

        gate.SetResult(TestData.OverviewWith());
        cut.WaitForAssertion(() => cut.Find(".k8s-tile-nodes").TextContent.Should().Contain("all ready"));
    }

    [Fact]
    public void Refresh_invalidates_the_access_cache_and_reloads()
    {
        Arrange(TestData.Overview());
        var cut = RenderPage();
        AccessCache.Set(ConnectionId, [], K8sPermissions.All);

        cut.Find(".k8s-overview-refresh").Click();

        cut.WaitForAssertion(() => OverviewCalls().Should().Be(2));
        AccessCache.TryGet(ConnectionId, [], out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_load_completing_after_dispose_does_not_start_a_retry_timer()
    {
        var gate = new TaskCompletionSource<K8sOverview>();
        Operations.GetOverviewAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var cut = Render<OverviewPage>();
        cut.WaitForState(() => OverviewCalls() > 0, TimeSpan.FromSeconds(5));

        await DisposeComponentsAsync();
        gate.SetException(new TimeoutException("late"));
        await Task.Delay(100);
        Time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100);

        OverviewCalls().Should().Be(1);
    }

    [Fact]
    public void The_cpu_caption_reads_in_cores_below_one_core()
    {
        Arrange(TestData.OverviewWith(nodes: [TestData.Node() with { CpuAllocatableMilli = 2000, CpuRequestedMilli = 250 }]));
        var cut = RenderPage();

        cut.Find(".k8s-tile-cpu").TextContent.Should().Contain("0.3 / 2 cores · limits 0");
    }

    [Fact]
    public async Task A_visibility_hook_resolving_after_dispose_is_released_at_once()
    {
        // bUnit's module interop completes module calls synchronously, so a substitute runtime holds watchVisibility open.
        Arrange(TestData.Overview());
        var watch = new TaskCompletionSource<IJSObjectReference>();
        var module = Substitute.For<IJSObjectReference>();
        module.InvokeAsync<IJSObjectReference>("watchVisibility", Arg.Any<object?[]?>()).Returns(_ => new ValueTask<IJSObjectReference>(watch.Task));
        var js = Substitute.For<IJSRuntime>();
        js.InvokeAsync<IJSObjectReference>("import", Arg.Any<object?[]?>()).Returns(new ValueTask<IJSObjectReference>(module));
        Services.AddSingleton(js);
        var handle = Substitute.For<IJSObjectReference>();
        RenderPage();
        await Task.Delay(50);
        module.ReceivedCalls().Should().Contain(c => Equals(c.GetArguments()[0], "watchVisibility"));

        await DisposeComponentsAsync();
        watch.SetResult(handle);
        await Task.Delay(100);

        handle.ReceivedCalls().Should().Contain(c => Equals(c.GetArguments().FirstOrDefault(), "dispose"));
        await handle.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task The_poller_stops_on_dispose()
    {
        Arrange(TestData.Overview());
        RenderPage();

        await DisposeComponentsAsync();
        Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        OverviewCalls().Should().Be(1);
    }

    [Fact]
    public void No_connections_renders_no_body()
    {
        Connections.ListAsync(KubernetesPlugin.Kind, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectionInfo>>([]));
        var cut = Render<OverviewPage>();
        cut.WaitForState(() => cut.FindAll(".k8s-no-connections").Count > 0, TimeSpan.FromSeconds(5));

        cut.FindAll(".k8s-overview-body").Should().BeEmpty();
        cut.FindAll(".mud-skeleton").Should().BeEmpty();
        cut.FindAll(".k8s-live").Should().BeEmpty();
    }
}
