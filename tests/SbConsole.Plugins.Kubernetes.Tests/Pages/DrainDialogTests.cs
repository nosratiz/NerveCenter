using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Sdk;
using static SbConsole.Plugins.Kubernetes.Tests.Nodes.DrainPlannerTests;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class DrainDialogTests : K8sPageTestBase
{
    private const string NodeName = "aks-pool1-38211-vmss00000a";

    private static K8sNodeInfo NotReadyNode => TestData.Nodes().Single(n => n.Name == NodeName);

    /// <summary>Mockup 3h: 31 evictable Deployment pods, ledger-1 and kafka-2 blocked by PDBs, 3 DaemonSet pods.</summary>
    public DrainDialogTests()
    {
        var pods = new List<K8sPodSummary>
        {
            Pod("payments", "ledger-1", "StatefulSet", "ledger", new Dictionary<string, string> { ["app"] = "ledger" }),
            Pod("platform", "kafka-2", "StatefulSet", "kafka", new Dictionary<string, string> { ["app"] = "kafka" }),
            Pod("kube-system", "kube-proxy-4x8lq", "DaemonSet", "kube-proxy"),
            Pod("kube-system", "azure-ip-masq-agent-x1", "DaemonSet", "azure-ip-masq-agent"),
            Pod("kube-system", "cloud-node-manager-x2", "DaemonSet", "cloud-node-manager"),
        };
        pods.AddRange(Enumerable.Range(1, 31).Select(i => Pod("checkout", $"checkout-api-{i:d2}", "Deployment", "checkout-api")));
        Operations.GetDrainInputsAsync(Secret, NodeName, Arg.Any<CancellationToken>()).Returns(_ => Inputs(pods,
        [
            new K8sPdb("payments", "ledger", new Dictionary<string, string> { ["app"] = "ledger" }, 0, "minAvailable 3", 3, 3),
            new K8sPdb("platform", "kafka", new Dictionary<string, string> { ["app"] = "kafka" }, 0, "maxUnavailable 0", 3, 3),
        ], new Dictionary<string, int> { ["Deployment/checkout/checkout-api"] = 40 }) with { Node = NotReadyNode });
        Operations.EvictPodAsync(Secret, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(K8sEvictionOutcome.Evicted);
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Cut, IDialogReference Reference)> ShowAsync(bool prod = true)
    {
        var cut = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var connection = (await Connections.ListAsync(KubernetesPlugin.Kind))[0];
        if (!prod)
        {
            connection = connection with { Tags = [] };
        }

        IDialogReference reference = null!;
        await cut.InvokeAsync(async () => reference = await DrainDialog.ShowAsync(dialogs, connection, NotReadyNode));
        cut.WaitForAssertion(() => cut.FindAll(".k8s-drain-pod-row").Should().NotBeEmpty());
        return (cut, reference);
    }

    private static string Tile(IRenderedComponent<MudDialogProvider> cut, string cls) =>
        cut.Find($".{cls} .k8s-tile-number").TextContent.Trim();

    [Fact]
    public async Task Shows_the_title_intro_and_tiles_from_the_plan()
    {
        var (cut, _) = await ShowAsync();

        cut.Find(".k8s-drain-title").TextContent.Should().Contain("Drain node").And.Contain(NodeName);
        cut.FindAll(".k8s-drain-dialog .k8s-prod-chip").Should().HaveCount(1);
        cut.Find(".k8s-drain-intro").TextContent.Trim().Should().Be(
            "Cordons the node, then evicts every pod below through the Eviction API, honouring PodDisruptionBudgets. " +
            "33 pods will be rescheduled; 2 can't be and will block the drain unless you force. " +
            "This node is already NotReady, so the pods aren't serving anyway.");
        Tile(cut, "k8s-drain-evict").Should().Be("31");
        Tile(cut, "k8s-drain-ds").Should().Be("3");
        Tile(cut, "k8s-drain-blocked").Should().Be("2");
        Tile(cut, "k8s-drain-time").Should().Be("~3m");
        cut.Find(".k8s-drain-evict .k8s-tile-label").TextContent.Should().Be("Will evict");
        cut.Find(".k8s-drain-ds .k8s-tile-label").TextContent.Should().Be("DaemonSet (ignored)");
        cut.Find(".k8s-drain-blocked .k8s-tile-label").TextContent.Should().Be("Blocked by PDB");
        cut.Find(".k8s-drain-time .k8s-tile-label").TextContent.Should().Be("Est. time");
        cut.Find(".k8s-drain-audit").TextContent.Trim().Should()
            .Be("Audited as k8s.node.drain with your name, the node, the options and the list of evicted pods.");
    }

    [Fact]
    public async Task Pod_list_puts_blocked_pods_first_caps_at_thirty_and_filters()
    {
        var (cut, _) = await ShowAsync();

        var rows = cut.FindAll(".k8s-drain-pod-row");
        rows.Should().HaveCount(30);
        rows[0].ClassList.Should().Contain("k8s-drain-blocked-row");
        rows[0].TextContent.Should().Contain("payments").And.Contain("ledger-1").And.Contain("StatefulSet").And.Contain("PDB ledger minAvailable 3 · now 3");
        rows[1].TextContent.Should().Contain("kafka-2").And.Contain("PDB kafka maxUnavailable 0");
        rows[2].ClassList.Should().NotContain("k8s-drain-blocked-row");
        rows[2].TextContent.Should().Contain("checkout-api-01").And.Contain("1 of 40 reschedules");
        cut.Find(".k8s-drain-more").TextContent.Trim().Should().Be("6 more…");

        cut.Find(".k8s-drain-filter input").Input("kube-proxy");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-drain-pod-row").Should().ContainSingle()
            .Which.TextContent.Should().Contain("kube-proxy-4x8lq").And.Contain("DaemonSet").And.Contain("ignored"));
        cut.FindAll(".k8s-drain-more").Should().BeEmpty();
    }

    [Fact]
    public async Task Options_name_the_blocked_pods_and_grace_replans_the_estimate()
    {
        var (cut, _) = await ShowAsync();

        cut.Find(".k8s-drain-force").TextContent.Should().Contain("Force past PDBs (deletes ledger-1 and kafka-2)");
        cut.Find(".k8s-drain-emptydir").TextContent.Should().Contain("Delete emptyDir data");
        cut.Find(".k8s-drain-grace input").GetAttribute("value").Should().Be("30");

        cut.Find(".k8s-drain-grace input").Input("60");

        cut.WaitForAssertion(() => Tile(cut, "k8s-drain-time").Should().Be("~7m"));
    }

    [Fact]
    public async Task Delete_emptyDir_data_defaults_to_unchecked_like_kubectl()
    {
        Operations.GetDrainInputsAsync(Secret, NodeName, Arg.Any<CancellationToken>())
            .Returns(Inputs([Pod("checkout", "cache-1", "Deployment", "cache", emptyDir: true)]) with { Node = NotReadyNode });
        var (cut, _) = await ShowAsync(prod: false);

        ((AngleSharp.Html.Dom.IHtmlInputElement)cut.Find(".k8s-drain-emptydir input")).IsChecked.Should().BeFalse();

        cut.Find(".k8s-drain-run").Click();

        cut.WaitForAssertion(() => cut.Find(".k8s-drain-summary").TextContent.Should().Contain("1 skipped"), TimeSpan.FromSeconds(5));
        await Operations.DidNotReceiveWithAnyArgs().EvictPodAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Grace_period_cannot_go_below_one_second()
    {
        var (cut, _) = await ShowAsync();

        cut.Find(".k8s-drain-grace input").GetAttribute("min").Should().Be("1");
        cut.Find(".k8s-drain-grace input").Input("0");

        cut.WaitForAssertion(() => Tile(cut, "k8s-drain-time").Should().Be("~7s"));
    }

    [Fact]
    public async Task On_prod_the_drain_button_stays_disabled_until_the_exact_node_name_is_typed()
    {
        var (cut, _) = await ShowAsync(prod: true);

        cut.Find(".k8s-drain-confirm").TextContent.Should().Contain($"Type {NodeName} to confirm");
        cut.Find(".k8s-drain-run").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".k8s-drain-run").ClassList.Should().Contain("k8s-disabled-soft");

        cut.Find(".k8s-drain-confirm input").Input(NodeName[..^1]);
        cut.WaitForAssertion(() => cut.Find(".k8s-drain-run").HasAttribute("disabled").Should().BeTrue());

        cut.Find(".k8s-drain-confirm input").Input(NodeName);
        cut.WaitForAssertion(() => cut.Find(".k8s-drain-run").HasAttribute("disabled").Should().BeFalse());
        cut.Find(".k8s-drain-run").ClassList.Should().NotContain("k8s-disabled-soft");
    }

    [Fact]
    public async Task Not_prod_needs_no_typing()
    {
        var (cut, _) = await ShowAsync(prod: false);

        cut.FindAll(".k8s-drain-confirm").Should().BeEmpty();
        cut.FindAll(".k8s-drain-dialog .k8s-prod-chip").Should().BeEmpty();
        cut.Find(".k8s-drain-run").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Cordon_only_cordons_through_the_handler_and_closes()
    {
        var (cut, reference) = await ShowAsync();

        cut.Find(".k8s-cordon-only").Click();

        (await reference.Result)!.Canceled.Should().BeFalse();
        await Operations.Received(1).SetUnschedulableAsync(Secret, NodeName, true, Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("k8s.node.cordon", $"{ConnectionName}/{NodeName}", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().EvictPodAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Running_shows_progress_and_results_then_a_summary_and_close()
    {
        var (cut, reference) = await ShowAsync(prod: false);

        cut.Find(".k8s-drain-run").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-drain-summary").Should().HaveCount(1), TimeSpan.FromSeconds(5));
        cut.Find(".k8s-drain-progress-text").TextContent.Trim().Should().Be("33 / 33");
        cut.FindAll(".k8s-drain-result").Should().HaveCount(33);
        cut.Find(".k8s-drain-summary").TextContent.Should().Contain("33 evicted");
        await Operations.Received(1).SetUnschedulableAsync(Secret, NodeName, true, Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("k8s.node.drain", $"{ConnectionName}/{NodeName}", ActionRisk.Destructive, true,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await Confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default);

        cut.Find(".k8s-drain-close").Click();
        (await reference.Result)!.Canceled.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_cancels_the_running_drain()
    {
        var gate = new TaskCompletionSource<K8sEvictionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Operations.EvictPodAsync(Secret, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        var (cut, _) = await ShowAsync(prod: false);

        cut.FindAll(".mud-dialog .mud-button-close").Should().HaveCount(1, "the title-bar close is there before the drain starts");

        cut.Find(".k8s-drain-run").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-drain-stop").Should().HaveCount(1));
        cut.FindAll(".k8s-drain-cancel").Should().BeEmpty();
        cut.WaitForAssertion(() => cut.FindAll(".mud-dialog .mud-button-close").Should().BeEmpty("closing mid-drain would cancel it without a summary"));

        cut.Find(".k8s-drain-stop").Click();
        gate.SetResult(K8sEvictionOutcome.Evicted);

        cut.WaitForAssertion(() => cut.Find(".k8s-drain-summary").TextContent.Should().Contain("Stopped"), TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.FindAll(".mud-dialog .mud-button-close").Should().HaveCount(1));
        await Audit.Received(1).RecordAsync("k8s.node.drain", Arg.Any<string>(), ActionRisk.Destructive, false,
            Arg.Is<string?>(d => d!.Contains("cancelled after 5 of 33")), Arg.Any<CancellationToken>());
    }
}
