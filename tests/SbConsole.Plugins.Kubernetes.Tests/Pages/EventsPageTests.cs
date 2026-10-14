using System.Net;
using Bunit;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Overview;
using EventsPage = SbConsole.Plugins.Kubernetes.Pages.Events;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class EventsPageTests : K8sPageTestBase
{
    public EventsPageTests() =>
        Operations.ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.Events());

    private int EventCalls() =>
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ListEventsAsync));

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage()
    {
        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<EventsPage>(0);
            builder.CloseComponent();
        });
        cut.WaitForState(() => cut.FindAll(".k8s-ev-group, .k8s-ev-table, .k8s-ev-empty, .k8s-ev-error").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static IReadOnlyList<string> GroupNames(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindAll(".k8s-ev-group .k8s-ev-group-name").Select(e => e.TextContent.Trim()).ToList();

    private static AngleSharp.Dom.IElement Group(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".k8s-ev-group").Single(g => g.QuerySelector(".k8s-ev-group-name")!.TextContent.Trim() == name);

    private string Link(string href) => AttentionRules.WithConnection(ConnectionId)(href);

    [Fact]
    public void Defaults_to_warnings_with_counts_in_the_type_toggle()
    {
        var cut = RenderPage();

        cut.Find(".k8s-ev-type-all").TextContent.Should().Contain("All").And.Contain("43");
        cut.Find(".k8s-ev-type-warning").TextContent.Should().Contain("Warning").And.Contain("38");
        cut.Find(".k8s-ev-type-normal").TextContent.Should().Contain("Normal").And.Contain("5");
        cut.Find(".k8s-ev-type-warning").ClassList.Should().Contain("mud-toggle-item-selected");
        cut.FindAll(".k8s-ev-row").Should().HaveCount(38);
        cut.FindAll(".k8s-ev-row .k8s-ev-reason").Should().OnlyContain(r => r.ClassList.Contains("k8s-ev-reason-warning"));
        cut.Find(".k8s-ev-summary").TextContent.Should()
            .Contain("38 warnings on 9 objects · newest first · counts are count / series over the window");
        cut.Find(".k8s-ev-window").TextContent.Should().Contain("events usually expire after 1 h");
        Operations.Received(1).ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Groups_are_newest_first_and_the_first_three_are_expanded()
    {
        var cut = RenderPage();

        GroupNames(cut).Should().Equal(
            "payment-gateway-7d9f8-x2k4q", "prometheus-1", "payment-gateway", "aks-pool1-38211-vmss00000a",
            "checkout-api-6c8d7-r9p2m", "aks-pool1-38211-vmss000004", "checkout-api", "report-builder-2x8ql", "settlement-20261014-0200");
        var groups = cut.FindAll(".k8s-ev-group");
        groups.Take(3).Should().OnlyContain(g => g.ClassList.Contains("mud-panel-expanded"));
        groups.Skip(3).Should().OnlyContain(g => !g.ClassList.Contains("mud-panel-expanded"));
    }

    [Fact]
    public void Group_header_shows_kind_namespace_top_reasons_warning_count_and_age()
    {
        var cut = RenderPage();

        var header = Group(cut, "payment-gateway-7d9f8-x2k4q");
        header.QuerySelector(".k8s-ev-kind-chip")!.TextContent.Trim().Should().Be("Pod");
        header.QuerySelector(".k8s-ev-group-ns")!.TextContent.Trim().Should().Be("checkout");
        header.QuerySelector(".k8s-ev-top-reasons")!.TextContent.Trim().Should().Be("BackOff · Unhealthy");
        header.QuerySelector(".k8s-ev-warning-count")!.TextContent.Trim().Should().Be("16 warnings");
        header.QuerySelector(".k8s-ev-last")!.TextContent.Trim().Should().Be("last 2m");
        Group(cut, "settlement-20261014-0200").QuerySelector(".k8s-ev-warning-count")!.TextContent.Trim().Should().Be("1 warning");
        Group(cut, "aks-pool1-38211-vmss00000a").QuerySelector(".k8s-ev-group-ns").Should().BeNull("nodes are cluster-scoped");
    }

    [Fact]
    public void Group_headers_link_pods_deployments_and_nodes()
    {
        var cut = RenderPage();

        Group(cut, "payment-gateway-7d9f8-x2k4q").QuerySelector("a.k8s-ev-group-name")!.GetAttribute("href")
            .Should().Be(Link(KubernetesPlugin.PodHref("checkout", "payment-gateway-7d9f8-x2k4q")));
        Group(cut, "payment-gateway").QuerySelector("a.k8s-ev-group-name")!.GetAttribute("href")
            .Should().Be(Link(KubernetesPlugin.DeploymentHref("checkout", "payment-gateway")));
        Group(cut, "aks-pool1-38211-vmss00000a").QuerySelector("a.k8s-ev-group-name")!.GetAttribute("href")
            .Should().Be(Link(KubernetesPlugin.NodesHref));
        Group(cut, "checkout-api").QuerySelector("a.k8s-ev-group-name").Should().BeNull("HPAs have no page");
    }

    [Fact]
    public void Rows_show_age_reason_message_count_chip_and_reporter()
    {
        var cut = RenderPage();

        var rows = Group(cut, "payment-gateway-7d9f8-x2k4q").QuerySelectorAll(".k8s-ev-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".k8s-ev-age")!.TextContent.Trim().Should().Be("2m");
        rows[0].QuerySelector(".k8s-ev-reason")!.TextContent.Trim().Should().Be("BackOff");
        rows[0].QuerySelector(".k8s-ev-message")!.TextContent.Should().StartWith("Back-off restarting failed container gateway");
        rows[0].QuerySelector(".k8s-ev-count")!.TextContent.Trim().Should().Be("×14");
        rows[0].QuerySelector(".k8s-ev-reporter")!.TextContent.Trim().Should().Be("kubelet");
        Group(cut, "aks-pool1-38211-vmss00000a").QuerySelectorAll(".k8s-ev-row")[0].QuerySelector(".k8s-ev-count")
            .Should().BeNull("a single occurrence has no count chip");
    }

    [Fact]
    public void All_type_shows_normal_events_without_the_error_colour()
    {
        var cut = RenderPage();

        cut.Find(".k8s-ev-type-all").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-ev-row").Should().HaveCount(43));
        cut.FindAll(".k8s-ev-reason:not(.k8s-ev-reason-warning)").Should().HaveCount(5);
        Operations.Received().ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Search_filters_through_the_handler()
    {
        var cut = RenderPage();

        cut.Find(".k8s-ev-search input").Input("metrics-server");
        // MudBlazor's 300 ms debounce runs on the injected TimeProvider.
        Time.Advance(TimeSpan.FromMilliseconds(300));

        cut.WaitForAssertion(() => GroupNames(cut).Should().Equal("checkout-api"), TimeSpan.FromSeconds(3));
        cut.Find(".k8s-ev-summary").TextContent.Should().Contain("1 warning on 1 object");
    }

    [Fact]
    public async Task Kind_filter_narrows_the_groups()
    {
        var cut = RenderPage();

        var kind = cut.FindComponents<MudSelect<string>>().Single(s => s.Instance.Class!.Contains("k8s-ev-kind"));
        await cut.InvokeAsync(() => kind.Instance.ValueChanged.InvokeAsync("Node"));

        cut.WaitForAssertion(() => GroupNames(cut).Should().Equal("aks-pool1-38211-vmss00000a", "aks-pool1-38211-vmss000004"));
    }

    [Fact]
    public async Task Window_select_refetches_with_that_window()
    {
        var cut = RenderPage();

        var window = cut.FindComponents<MudSelect<int>>().Single(s => s.Instance.Class!.Contains("k8s-ev-window"));
        await cut.InvokeAsync(() => window.Instance.ValueChanged.InvokeAsync(6));

        cut.WaitForAssertion(() => Operations.Received(1).ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), TimeSpan.FromHours(6),
            Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Group_by_reason_groups_across_objects()
    {
        var cut = RenderPage();

        cut.Find(".k8s-ev-groupby-reason").Click();

        cut.WaitForAssertion(() => GroupNames(cut).Should().StartWith("BackOff"));
        GroupNames(cut).Should().Contain("Unhealthy").And.OnlyHaveUniqueItems();
        cut.FindAll(".k8s-ev-group .k8s-ev-kind-chip").Should().BeEmpty();
    }

    [Fact]
    public void Group_by_none_renders_a_flat_table()
    {
        var cut = RenderPage();

        cut.Find(".k8s-ev-groupby-none").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-ev-table").Should().ContainSingle());
        cut.FindAll(".k8s-ev-group").Should().BeEmpty();
        var rows = cut.FindAll(".k8s-ev-table .k8s-ev-row");
        rows.Should().HaveCount(38);
        rows[0].TextContent.Should().Contain("payment-gateway-7d9f8-x2k4q").And.Contain("BackOff");
    }

    [Fact]
    public void Live_polls_every_five_seconds_and_off_stops_polling()
    {
        var cut = RenderPage();
        EventCalls().Should().Be(1);

        Time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => EventCalls().Should().Be(2));

        cut.Find(".k8s-ev-live input").Change(false);
        Time.Advance(TimeSpan.FromSeconds(30));
        Thread.Sleep(100);
        EventCalls().Should().Be(2);

        cut.Find(".k8s-ev-live input").Change(true);
        cut.WaitForAssertion(() => EventCalls().Should().Be(3), TimeSpan.FromSeconds(2));
        Time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => EventCalls().Should().Be(4));
    }

    [Fact]
    public void Footnote_says_events_are_the_clusters_own_and_nothing_is_audited()
    {
        var cut = RenderPage();

        cut.Find(".k8s-ev-footnote").TextContent.Should()
            .Contain("Events are the cluster's own, retained ~1h by the API server. The console doesn't store them; the audit log is for console actions.")
            .And.Contain("Read-only page · nothing here is audited");
    }

    [Fact]
    public void A_failed_list_shows_the_friendly_error()
    {
        Operations.ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpOperationException("raw")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), "{\"kind\":\"Status\",\"message\":\"forbidden\"}"),
            });

        var cut = RenderPage();

        cut.Find(".k8s-ev-error").TextContent.Should().Contain("Can't list events.").And.NotContain("raw");
    }

    [Fact]
    public void No_matching_events_says_so()
    {
        Operations.ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<K8sEvent>)[]);

        var cut = RenderPage();

        cut.Find(".k8s-ev-empty").TextContent.Should().Contain("No Warning events in the last 1h.");
    }

    [Fact]
    public void Namespace_selection_is_passed_to_the_fetch()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/p/kubernetes/events?ns=checkout");

        RenderPage();

        Operations.Received(1).ListEventsAsync(Secret, Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "checkout" })),
            TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }
}
