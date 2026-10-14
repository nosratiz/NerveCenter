using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Plugins.Kubernetes.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class K8sConnectionPickerTests : K8sPageTestBase
{
    private readonly Guid _secondId = Guid.NewGuid();

    private void TwoConnections() =>
        Connections.ListAsync(KubernetesPlugin.Kind, Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, KubernetesPlugin.Kind, ["prod"],
                Summary: new Dictionary<string, string> { ["Server"] = "aks-prod-eu.example", ["Context"] = "aks-prod-eu-admin" }),
            new ConnectionInfo(_secondId, "k3s-local", KubernetesPlugin.Kind, [],
                Summary: new Dictionary<string, string> { ["Server"] = "localhost:6443", ["Context"] = "k3s-viewer" }),
        ]);

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPicker(List<ConnectionInfo> selections) =>
        RenderWithPopovers(builder =>
        {
            builder.OpenComponent<K8sConnectionPicker>(0);
            builder.AddAttribute(1, nameof(K8sConnectionPicker.SelectionChanged),
                EventCallback.Factory.Create<ConnectionInfo>(this, c => selections.Add(c)));
            builder.CloseComponent();
        });

    private static async Task OpenMenuAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, int options)
    {
        await cut.Find(".k8s-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        cut.WaitForAssertion(() => cut.FindAll(".k8s-connection-option").Should().HaveCount(options));
    }

    [Fact]
    public async Task Selects_the_first_connection_and_shows_server_context_and_prod()
    {
        TwoConnections();
        var selections = new List<ConnectionInfo>();
        var cut = RenderPicker(selections);

        cut.WaitForAssertion(() => selections.Should().ContainSingle().Which.Name.Should().Be(ConnectionName));
        cut.Find(".k8s-selected-connection").TextContent.Should().Be(ConnectionName);
        cut.Find(".k8s-prod-chip").TextContent.Should().Contain("PROD");
        cut.Find(".k8s-server-chip").TextContent.Should().Contain("aks-prod-eu.example").And.Contain("aks-prod-eu-admin");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"connectionId={ConnectionId}");
        await OpenMenuAsync(cut, 2);
        cut.FindAll(".k8s-connection-option").Should().Contain(o => o.TextContent.Contains("k3s-local"));
    }

    [Fact]
    public void Selects_from_the_connectionId_query_string()
    {
        TwoConnections();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/kubernetes/overview?connectionId={_secondId}");
        var selections = new List<ConnectionInfo>();
        var cut = RenderPicker(selections);

        cut.WaitForAssertion(() => selections.Should().ContainSingle().Which.Id.Should().Be(_secondId));
        cut.Find(".k8s-selected-connection").TextContent.Should().Be("k3s-local");
        cut.FindAll(".k8s-prod-chip").Should().BeEmpty();
    }

    [Fact]
    public async Task Shows_a_red_dot_for_a_connection_whose_last_load_failed()
    {
        TwoConnections();
        await OverviewSnapshotStore.WriteGoodAsync(Store, _secondId, new OverviewSnapshot(TestData.Overview(), Time.GetUtcNow(), []), default);
        await OverviewSnapshotStore.WriteFailureAsync(Store, _secondId, Time.GetUtcNow().AddMinutes(5), "down", default);
        var cut = RenderPicker([]);

        await OpenMenuAsync(cut, 2);
        var options = cut.FindAll(".k8s-connection-option");
        options[0].QuerySelector(".k8s-unreachable-dot").Should().BeNull();
        options[1].QuerySelector(".k8s-unreachable-dot").Should().NotBeNull();
    }

    [Fact]
    public async Task A_failure_older_than_the_last_good_load_shows_no_dot()
    {
        TwoConnections();
        await OverviewSnapshotStore.WriteFailureAsync(Store, _secondId, Time.GetUtcNow().AddMinutes(-5), "down", default);
        await OverviewSnapshotStore.WriteGoodAsync(Store, _secondId, new OverviewSnapshot(TestData.Overview(), Time.GetUtcNow(), []), default);
        var cut = RenderPicker([]);

        await OpenMenuAsync(cut, 2);
        cut.FindAll(".k8s-unreachable-dot").Should().BeEmpty();
    }

    [Fact]
    public void Shows_an_empty_state_linking_to_connections()
    {
        Connections.ListAsync(KubernetesPlugin.Kind, Arg.Any<CancellationToken>()).Returns([]);
        var selections = new List<ConnectionInfo>();
        var cut = RenderPicker(selections);

        cut.WaitForAssertion(() => cut.Find(".k8s-no-connections").TextContent.Should().Contain("No Kubernetes connections"));
        cut.Find(".k8s-no-connections a").GetAttribute("href").Should().Be("/connections");
        selections.Should().BeEmpty();
    }
}
