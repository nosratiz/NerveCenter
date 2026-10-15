using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Architecture;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Namespaces;
using SbConsole.Plugins.Kubernetes.Tests.Architecture;
using SbConsole.Sdk;
using ArchitecturePage = SbConsole.Plugins.Kubernetes.Pages.Architecture;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class ArchitecturePageTests : K8sPageTestBase
{
    private static readonly string Gateway = ArchTestData.W("checkout", "payment-gateway");
    private static readonly string Cart = ArchTestData.W("checkout", "cart");

    public ArchitecturePageTests()
    {
        Store.Values[NamespaceSelectionStore.Key(ConnectionId, UserName)] = JsonSerializer.Serialize(ArchTestData.Scope);
        Operations.GetArchitectureInputsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ArchTestData.Cluster());
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage()
    {
        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<ArchitecturePage>(0);
            builder.CloseComponent();
        });
        cut.WaitForState(() => cut.FindAll(".k8s-arch-node, .k8s-arch-empty, .k8s-arch-error").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static AngleSharp.Dom.IElement Node(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string id) =>
        cut.Find($".k8s-arch-node[data-node='{id}']");

    private static IReadOnlyList<string> NodeNames(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindAll(".k8s-arch-node .k8s-arch-node-title").Select(e => e.TextContent.Trim()).ToList();

    [Fact]
    public void Renders_namespace_lanes_the_side_column_and_counts()
    {
        var cut = RenderPage();

        cut.FindAll(".k8s-arch-lane-label").Select(l => l.TextContent).Should()
            .Equal("checkout", "payments", ArchitectureLayout.SideLabel);
        NodeNames(cut).Should().Contain(["shop", "checkout-api", "payment-gateway", "cart-redis", "kafka", "pg-main", "hooks.example.com"]);
        cut.FindAll(".k8s-arch-edges path[data-edge]").Should().HaveCount(11);
        cut.Find(".k8s-arch-edge-counts").TextContent.Should().Be("11 edges · 11 detected · 0 manual");
        cut.Find(".k8s-arch-undescribed").TextContent.Should().Contain("8 undescribed");
        Node(cut, Gateway).ClassList.Should().Contain("k8s-arch-failing");
        Node(cut, ArchTestData.W("payments", "webhooks")).ClassList.Should().Contain("k8s-arch-idle");
        Operations.Received().GetArchitectureInputsAsync(Secret,
            Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(ArchTestData.Scope)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Selecting_a_node_opens_the_inspector_and_highlights_its_edges()
    {
        var cut = RenderPage();

        Node(cut, Gateway).Click();

        Node(cut, Gateway).ClassList.Should().Contain("k8s-arch-node-selected");
        cut.Find(".k8s-arch-insp-name").TextContent.Should().Be("payment-gateway");
        cut.Find(".k8s-arch-insp-health").TextContent.Should().Contain("unavailable");
        cut.Find(".k8s-arch-insp-version").TextContent.Should().Be("2.14.1");
        cut.Find(".k8s-arch-open-deployment").GetAttribute("href").Should()
            .Be($"/p/kubernetes/workloads/deployments/checkout/payment-gateway?connectionId={ConnectionId}");
        var depends = cut.FindAll(".k8s-arch-depends-on .k8s-arch-dep");
        depends.Select(d => d.QuerySelector(".k8s-arch-dep-name")!.TextContent).Should().BeEquivalentTo("cart-redis", "kafka");
        depends.Select(d => d.QuerySelector(".k8s-arch-src")!.TextContent).Should().BeEquivalentTo("env REDIS_URL", "env KAFKA_BROKERS");
        cut.FindAll(".k8s-arch-used-by .k8s-arch-dep-name").Select(d => d.TextContent).Should().Equal("checkout-api");
        cut.FindAll(".k8s-arch-edge-on").Should().HaveCount(3);
        cut.FindAll(".k8s-arch-edge-label").Select(l => l.TextContent).Should().BeEquivalentTo("redis · 6379", "kafka · 9092", "HTTP · 8080");
        cut.Find(".k8s-arch-desc-none").TextContent.Should().Be("No description yet.");
    }

    [Fact]
    public void Saving_a_description_shows_it_with_its_version_stamp()
    {
        var cut = RenderPage();
        Node(cut, Gateway).Click();

        cut.Find(".k8s-arch-desc-edit").Click();
        cut.Find(".k8s-arch-desc-editor textarea").Input("Fronts Stripe for card payments.");
        cut.Find(".k8s-arch-desc-save").Click();

        cut.WaitForAssertion(() => cut.Find(".k8s-arch-desc-text").TextContent.Should().Be("Fronts Stripe for card payments."));
        cut.Find(".k8s-arch-desc-stamp").TextContent.Should().Be("v1 · admin · 0s ago");
        Node(cut, Gateway).QuerySelector(".k8s-arch-described").Should().NotBeNull();
        cut.Find(".k8s-arch-undescribed").TextContent.Should().Contain("7 undescribed");
        Audit.Received(1).RecordAsync("k8s.arch.edit", "aks-prod-eu/checkout/payment-gateway", ActionRisk.Mutating, true, "description v1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_conflicting_save_keeps_the_editor_open_with_the_reason()
    {
        var cut = RenderPage();
        Node(cut, Gateway).Click();
        cut.Find(".k8s-arch-desc-edit").Click();
        Store.Values[ArchitectureStore.DescriptionsKey(ConnectionId)] = JsonSerializer.Serialize(
            new Dictionary<string, ArchDescription> { [Gateway] = new("theirs", 1, "b.lee", Time.GetUtcNow()) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        cut.Find(".k8s-arch-desc-editor textarea").Input("mine");
        cut.Find(".k8s-arch-desc-save").Click();

        cut.WaitForAssertion(() => cut.Find(".k8s-arch-insp-error").TextContent.Should().Contain("b.lee saved v1 since you opened it"));
        cut.FindAll(".k8s-arch-desc-editor").Should().ContainSingle();
    }

    [Fact]
    public void Dismissing_a_detected_edge_hides_it_until_restored()
    {
        var cut = RenderPage();
        Node(cut, Cart).Click();

        cut.Find(".k8s-arch-depends-on .k8s-arch-dep-remove").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-arch-depends-on .k8s-arch-dep").Should().BeEmpty());
        cut.Find(".k8s-arch-dismissed").TextContent.Should().Contain("1 dismissed edge");
        cut.Find(".k8s-arch-edge-counts").TextContent.Should().StartWith("10 edges");

        cut.Find(".k8s-arch-restore").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-arch-depends-on .k8s-arch-dep").Should().ContainSingle());
        cut.FindAll(".k8s-arch-dismissed").Should().BeEmpty();
    }

    [Fact]
    public async Task Adding_an_external_dependency_draws_a_manual_edge()
    {
        var cut = RenderPage();
        Node(cut, Gateway).Click();
        cut.Find(".k8s-arch-add").Click();

        var to = cut.FindComponents<MudSelect<string>>().Single(s => s.Instance.Class == "k8s-arch-add-to");
        await cut.InvokeAsync(() => to.Instance.ValueChanged.InvokeAsync("__external__"));
        cut.Find(".k8s-arch-add-host input").Input("https://api.stripe.com");
        cut.Find(".k8s-arch-add-label input").Input("HTTPS");
        cut.Find(".k8s-arch-add-save").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-arch-node[data-node='x:api.stripe.com']").Should().ContainSingle());
        var row = cut.FindAll(".k8s-arch-depends-on .k8s-arch-dep").Single(d => d.QuerySelector(".k8s-arch-dep-name")!.TextContent == "api.stripe.com");
        row.QuerySelector(".k8s-arch-src")!.TextContent.Should().Be("manual");
        cut.Find(".k8s-arch-edge-counts").TextContent.Should().Be("12 edges · 11 detected · 1 manual");
    }

    [Fact]
    public void Store_and_external_toggles_hide_those_nodes()
    {
        var cut = RenderPage();

        cut.Find(".k8s-arch-show-stores input").Change(false);
        cut.Find(".k8s-arch-show-external input").Change(false);

        NodeNames(cut).Should().NotContain(["cart-redis", "kafka", "pg-main", "hooks.example.com"]);
        cut.FindAll(".k8s-arch-lane-side").Should().BeEmpty();
    }

    [Fact]
    public void Team_grouping_relabels_the_lanes()
    {
        var cut = RenderPage();

        cut.Find(".k8s-arch-groupby-team").Click();

        cut.FindAll(".k8s-arch-lane:not(.k8s-arch-lane-side) .k8s-arch-lane-label").Select(l => l.TextContent)
            .Should().Equal("payments", "storefront", "no team");
    }

    [Fact]
    public void Find_dims_the_rest_and_selects_a_single_match()
    {
        var cut = RenderPage();

        cut.Find(".k8s-arch-find input").Input("ledg");
        // MudBlazor's debounce runs on the injected TimeProvider.
        Time.Advance(TimeSpan.FromMilliseconds(200));

        cut.WaitForAssertion(() => cut.Find(".k8s-arch-insp-name").TextContent.Should().Be("ledger"), TimeSpan.FromSeconds(5));
        Node(cut, Gateway).ClassList.Should().Contain("k8s-arch-node-dim");
        Node(cut, ArchTestData.W("payments", "ledger")).ClassList.Should().NotContain("k8s-arch-node-dim");
    }

    [Fact]
    public void A_failed_load_shows_the_reason()
    {
        Operations.GetArchitectureInputsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("nope"));

        var cut = RenderPage();

        cut.Find(".k8s-arch-error").TextContent.Should().Contain("Can't reach the cluster.");
        cut.FindAll(".k8s-arch-node").Should().BeEmpty();
    }
}
