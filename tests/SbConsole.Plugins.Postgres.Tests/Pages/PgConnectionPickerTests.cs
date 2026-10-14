using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Postgres.Components;
using SbConsole.Plugins.Postgres.Overview;
using SbConsole.Plugins.Postgres.Tests.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

public class PgConnectionPickerTests : PgPageTestBase
{
    private readonly Guid _secondId = Guid.NewGuid();

    private void TwoConnections() =>
        Connections.ListAsync("postgres", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "postgres", ["prod"], Summary: new Dictionary<string, string> { ["Server"] = "db:5432", ["Mode"] = "read-write" }),
            new ConnectionInfo(_secondId, "pg-analytics", "postgres", [], Summary: new Dictionary<string, string> { ["Server"] = "an:5432", ["Mode"] = "read-only" }),
        ]);

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPicker(List<ConnectionInfo> selections) =>
        RenderWithPopovers(builder =>
        {
            builder.OpenComponent<PgConnectionPicker>(0);
            builder.AddAttribute(1, nameof(PgConnectionPicker.SelectionChanged),
                EventCallback.Factory.Create<ConnectionInfo>(this, c => selections.Add(c)));
            builder.CloseComponent();
        });

    private static async Task OpenMenuAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, int options)
    {
        await cut.Find(".pg-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        cut.WaitForAssertion(() => cut.FindAll(".pg-connection-option").Should().HaveCount(options));
    }

    [Fact]
    public async Task Lists_connections_selects_the_first_and_shows_server_mode_and_prod()
    {
        TwoConnections();
        var selections = new List<ConnectionInfo>();
        var cut = RenderPicker(selections);

        cut.WaitForAssertion(() => selections.Should().ContainSingle().Which.Name.Should().Be(ConnectionName));
        cut.Find(".pg-selected-connection").TextContent.Should().Be(ConnectionName);
        cut.Find(".pg-prod-chip").Should().NotBeNull();
        cut.Find(".pg-server-chip").TextContent.Should().Contain("db:5432").And.Contain("read-write");
        await OpenMenuAsync(cut, 2);
        cut.FindAll(".pg-connection-option").Select(o => o.TextContent.Trim()).Should().Contain(t => t.Contains("pg-analytics"));
    }

    [Fact]
    public void Selects_from_the_connectionId_query_string()
    {
        TwoConnections();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/postgres/overview?connectionId={_secondId}");
        var selections = new List<ConnectionInfo>();
        var cut = RenderPicker(selections);

        cut.WaitForAssertion(() => selections.Should().ContainSingle().Which.Id.Should().Be(_secondId));
        cut.Find(".pg-selected-connection").TextContent.Should().Be("pg-analytics");
        cut.FindAll(".pg-prod-chip").Should().BeEmpty();
    }

    [Fact]
    public async Task Shows_a_red_dot_for_a_connection_whose_last_load_failed()
    {
        TwoConnections();
        await OverviewSnapshotStore.WriteGoodAsync(Store, _secondId, new OverviewSnapshot(OverviewTestData.Overview(), Time.GetUtcNow()), default);
        await OverviewSnapshotStore.WriteFailureAsync(Store, _secondId, Time.GetUtcNow().AddMinutes(5), "down", default);
        var cut = RenderPicker([]);

        await OpenMenuAsync(cut, 2);
        var options = cut.FindAll(".pg-connection-option");
        options[0].QuerySelector(".pg-unreachable-dot").Should().BeNull();
        options[1].QuerySelector(".pg-unreachable-dot").Should().NotBeNull();
    }

    [Fact]
    public async Task A_failure_older_than_the_last_good_load_shows_no_dot()
    {
        TwoConnections();
        await OverviewSnapshotStore.WriteFailureAsync(Store, _secondId, Time.GetUtcNow().AddMinutes(-5), "down", default);
        await OverviewSnapshotStore.WriteGoodAsync(Store, _secondId, new OverviewSnapshot(OverviewTestData.Overview(), Time.GetUtcNow()), default);
        var cut = RenderPicker([]);

        await OpenMenuAsync(cut, 2);
        cut.FindAll(".pg-unreachable-dot").Should().BeEmpty();
    }

    [Fact]
    public void Shows_an_empty_state_linking_to_connections()
    {
        Connections.ListAsync("postgres", Arg.Any<CancellationToken>()).Returns([]);
        var selections = new List<ConnectionInfo>();
        var cut = RenderPicker(selections);

        cut.WaitForAssertion(() => cut.Find(".pg-no-connections").TextContent.Should().Contain("No PostgreSQL connections"));
        cut.Find(".pg-no-connections a").GetAttribute("href").Should().Be("/connections");
        selections.Should().BeEmpty();
    }
}
