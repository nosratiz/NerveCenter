using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Components;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

public class SqliteConnectionPickerTests : SqlitePageTestBase
{
    private readonly Guid _secondId = Guid.NewGuid();

    private IRenderedComponent<SqliteConnectionPicker> RenderPicker(List<ConnectionInfo> published) =>
        Render<SqliteConnectionPicker>(p => p.Add(x => x.SelectionChanged, EventCallback.Factory.Create<ConnectionInfo>(this, published.Add)));

    private void TwoConnections() =>
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", ["prod"], Summary: new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-only" }),
            new ConnectionInfo(_secondId, "sqlite-scratch", "sqlite", [], Summary: new Dictionary<string, string> { ["File"] = "scratch.db", ["Mode"] = "read-only" }),
        ]);

    [Fact]
    public void Publishes_the_first_connection_and_shows_its_file_and_prod_tag()
    {
        var published = new List<ConnectionInfo>();

        var cut = RenderPicker(published);

        published.Should().ContainSingle().Which.Id.Should().Be(ConnectionId);
        cut.Find(".sqlite-selected-connection").TextContent.Should().Be(ConnectionName);
        cut.Find(".sqlite-prod-chip").TextContent.Should().Be("prod");
        cut.Find(".sqlite-file-chip").TextContent.Should().Contain("reports.db").And.Contain("read-only");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"connectionId={ConnectionId}");
    }

    [Fact]
    public void Honors_the_connection_id_in_the_query_string()
    {
        TwoConnections();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/sqlite/overview?connectionId={_secondId}");
        var published = new List<ConnectionInfo>();

        var cut = RenderPicker(published);

        published.Single().Id.Should().Be(_secondId);
        cut.FindAll(".sqlite-prod-chip").Should().BeEmpty();
    }

    [Fact]
    public void Explains_when_there_are_no_sqlite_connections()
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(Array.Empty<ConnectionInfo>());
        var published = new List<ConnectionInfo>();

        var cut = RenderPicker(published);

        published.Should().BeEmpty();
        cut.Find(".sqlite-no-connections").TextContent.Should().Contain("No SQLite connections yet");
    }
}
