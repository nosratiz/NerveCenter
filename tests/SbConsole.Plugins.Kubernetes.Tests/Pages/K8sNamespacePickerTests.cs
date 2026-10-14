using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Plugins.Kubernetes.Namespaces;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class K8sNamespacePickerTests : K8sPageTestBase
{
    private readonly List<IReadOnlyList<string>> _raised = [];

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPicker() =>
        RenderWithPopovers(builder =>
        {
            builder.OpenComponent<K8sNamespacePicker>(0);
            builder.AddAttribute(1, nameof(K8sNamespacePicker.ConnectionId), (Guid?)ConnectionId);
            builder.AddAttribute(2, nameof(K8sNamespacePicker.SelectionChanged),
                EventCallback.Factory.Create<IReadOnlyList<string>>(this, s => _raised.Add(s)));
            builder.CloseComponent();
        });

    private static void Open(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        cut.Find(".k8s-ns-picker div.mud-input-control.mud-select, div.mud-input-control.mud-select.k8s-ns-picker")
            .MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".k8s-ns-option").Should().NotBeEmpty());
    }

    private static void Choose(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".k8s-ns-option").First(o => o.QuerySelector(".k8s-ns-name")?.TextContent.Trim() == name).Click();

    private Task<IReadOnlyList<string>> StoredAsync() => NamespaceSelectionStore.GetAsync(Store, ConnectionId, UserName, default);

    [Fact]
    public void Loads_namespaces_with_pod_counts_and_starts_on_all()
    {
        var cut = RenderPicker();

        cut.WaitForAssertion(() => _raised.Should().ContainSingle().Which.Should().BeEmpty());
        cut.Find(".k8s-ns-trigger").TextContent.Should().Contain("All namespaces");
        Open(cut);
        cut.Find(".k8s-ns-all").TextContent.Should().Contain("All namespaces (3)");
        var options = cut.FindAll(".k8s-ns-option");
        options.Should().HaveCount(3);
        options[0].TextContent.Should().Contain("checkout").And.Contain("18");
        options[2].TextContent.Should().Contain("payments-canary").And.Contain("2");
        cut.Find(".k8s-ns-caption").TextContent.Should().Be("Selection is kept per cluster.");
    }

    [Fact]
    public async Task The_ns_query_string_wins_over_the_stored_selection_and_unknown_names_are_dropped()
    {
        await NamespaceSelectionStore.SetAsync(Store, ConnectionId, UserName, ["payments"], default);
        Nav.NavigateTo("/p/kubernetes/workloads?ns=checkout,nope");

        var cut = RenderPicker();

        cut.WaitForAssertion(() => _raised.Should().ContainSingle().Which.Should().Equal("checkout"));
        cut.Find(".k8s-ns-trigger").TextContent.Should().Contain("checkout");
    }

    [Fact]
    public async Task The_stored_selection_is_used_without_a_query_string()
    {
        await NamespaceSelectionStore.SetAsync(Store, ConnectionId, UserName, ["payments", "gone"], default);

        var cut = RenderPicker();

        cut.WaitForAssertion(() => _raised.Should().ContainSingle().Which.Should().Equal("payments"));
        cut.FindAll(".k8s-ns-trigger .k8s-ns-chip").Select(c => c.TextContent.Trim()).Should().Equal("payments");
        Uri.UnescapeDataString(Nav.Uri).Should().Contain("ns=payments");
    }

    [Fact]
    public async Task Choosing_two_namespaces_stores_them_updates_the_url_and_raises_the_event()
    {
        var cut = RenderPicker();
        cut.WaitForAssertion(() => _raised.Should().HaveCount(1));
        Open(cut);

        Choose(cut, "checkout");
        Choose(cut, "payments");

        cut.WaitForAssertion(() => _raised[^1].Should().Equal("checkout", "payments"));
        (await StoredAsync()).Should().Equal("checkout", "payments");
        Uri.UnescapeDataString(Nav.Uri).Should().Contain("ns=checkout,payments");
    }

    [Fact]
    public async Task All_namespaces_clears_the_selection()
    {
        await NamespaceSelectionStore.SetAsync(Store, ConnectionId, UserName, ["payments"], default);
        var cut = RenderPicker();
        cut.WaitForAssertion(() => _raised.Should().ContainSingle().Which.Should().Equal("payments"));
        Open(cut);

        cut.Find(".k8s-ns-all").Click();

        cut.WaitForAssertion(() => _raised[^1].Should().BeEmpty());
        (await StoredAsync()).Should().BeEmpty();
        Nav.Uri.Should().NotContain("ns=");
        cut.Find(".k8s-ns-trigger").TextContent.Should().Contain("All namespaces");
    }

    [Fact]
    public void The_search_box_filters_the_list()
    {
        var cut = RenderPicker();
        Open(cut);

        cut.Find(".k8s-ns-search input").Input("pay");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-ns-option").Should().HaveCount(2));
    }

    [Fact]
    public async Task A_load_failure_shows_an_error_chip_and_selects_all()
    {
        await NamespaceSelectionStore.SetAsync(Store, ConnectionId, UserName, ["payments"], default);
        Operations.ListNamespacesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("boom"));

        var cut = RenderPicker();

        cut.WaitForAssertion(() => cut.Find(".k8s-ns-error").Should().NotBeNull());
        _raised.Should().ContainSingle().Which.Should().BeEmpty();
    }
}
