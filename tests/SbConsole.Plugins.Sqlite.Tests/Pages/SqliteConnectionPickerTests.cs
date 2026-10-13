using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
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

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPickerWithPopovers(List<ConnectionInfo> published) =>
        RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SqliteConnectionPicker>(0);
            builder.AddAttribute(1, nameof(SqliteConnectionPicker.SelectionChanged), EventCallback.Factory.Create<ConnectionInfo>(this, published.Add));
            builder.CloseComponent();
        });

    private static async Task SelectSecondAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        await SettleAsync(cut);
        await cut.FindAll(".sqlite-connection-option")[1].ClickAsync(new());
    }

    [Fact]
    public async Task Clicking_the_chip_opens_the_menu()
    {
        // MudBlazor 9's activator div only handles keydown; a mouse click must come from the
        // ActivatorContent itself (context.ToggleAsync), or the menu never opens.
        TwoConnections();
        var cut = RenderPickerWithPopovers([]);
        await SettleAsync(cut);

        await cut.Find(".sqlite-connection-menu .sql-chip").ClickAsync(new());
        await SettleAsync(cut);

        cut.FindAll(".sqlite-connection-option").Should().HaveCount(2);
        cut.FindAll(".sqlite-new-database-option").Should().ContainSingle();
    }

    [Fact]
    public async Task Selecting_another_connection_publishes_it_and_updates_the_url()
    {
        TwoConnections();
        var published = new List<ConnectionInfo>();
        var cut = RenderPickerWithPopovers(published);

        await SelectSecondAsync(cut);

        published.Last().Id.Should().Be(_secondId);
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"connectionId={_secondId}");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public void An_unknown_or_invalid_connection_id_falls_back_to_the_first_connection(string value)
    {
        TwoConnections();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/sqlite/overview?connectionId={value}");
        var published = new List<ConnectionInfo>();

        RenderPicker(published);

        published.Should().ContainSingle().Which.Id.Should().Be(ConnectionId);
    }

    [Fact]
    public async Task Other_query_parameters_survive_a_selection()
    {
        TwoConnections();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/sqlite/tables?connectionId={ConnectionId}&table=events");
        var cut = RenderPickerWithPopovers([]);

        await SelectSecondAsync(cut);

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().Contain("table=events").And.Contain($"connectionId={_secondId}");
    }

    [Fact]
    public void A_connection_without_a_file_summary_renders_no_file_chip()
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
            [new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", [])]);
        var published = new List<ConnectionInfo>();

        var cut = RenderPicker(published);

        published.Should().ContainSingle();
        cut.FindAll(".sqlite-file-chip").Should().BeEmpty();
        cut.Find(".sqlite-selected-connection").TextContent.Should().Be(ConnectionName);
    }

    [Fact]
    public void Re_rendering_does_not_publish_again()
    {
        var published = new List<ConnectionInfo>();
        var cut = RenderPicker(published);

        cut.Render();

        published.Should().ContainSingle();
    }

    [Fact]
    public void Shows_the_page_busy_spinner_while_the_page_is_loading()
    {
        var cut = Render<SqliteConnectionPicker>(p => p.Add(x => x.Loading, true));

        cut.FindAll(".sqlite-page-busy").Should().ContainSingle();
    }

    // ---- New database entry points (spec §13) ----

    private readonly Guid _newId = Guid.NewGuid();

    private void ArrangeCreation()
    {
        Operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NewDatabaseCreated("/sqlite/tasks.db", "/sqlite/tasks.db", "tasks.db", 3));
        Creator.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // From now on the provider lists the new connection too.
                Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
                [
                    new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", ["prod"]),
                    new ConnectionInfo(_newId, "sqlite-tasks", "sqlite", [], Summary: new Dictionary<string, string> { ["File"] = "tasks.db", ["Mode"] = "read-write" }),
                ]);
                return new ConnectionCreateResult.Created(_newId);
            });
    }

    private static async Task CreateTasksAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        cut.Find(".sqlite-newdb-file").Input("tasks");
        await cut.Find(".sqlite-newdb-submit").ClickAsync(new());
        await SettleAsync(cut);
    }

    [Fact]
    public async Task The_connection_menu_offers_New_database_which_opens_the_dialog()
    {
        var cut = RenderPickerWithPopovers([]);
        cut.FindAll(".sqlite-newdb-dialog").Should().BeEmpty();

        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        await SettleAsync(cut);
        await cut.Find(".sqlite-new-database-option").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".sqlite-newdb-dialog .sql-dialog-title").TextContent.Should().Be("New database");
        await cut.Find(".sqlite-newdb-cancel").ClickAsync(new());
        cut.FindAll(".sqlite-newdb-dialog").Should().BeEmpty();
    }

    [Fact]
    public async Task The_no_connections_state_offers_New_database()
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(Array.Empty<ConnectionInfo>());
        var cut = RenderPicker([]);

        await cut.Find(".sqlite-new-database").ClickAsync(new());

        cut.FindAll(".sqlite-newdb-dialog").Should().ContainSingle();
    }

    [Fact]
    public async Task Creating_from_the_Overview_page_lists_and_selects_the_new_connection_in_place()
    {
        ArrangeCreation();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/p/sqlite/overview?connectionId={ConnectionId}");
        var published = new List<ConnectionInfo>();
        var cut = RenderPickerWithPopovers(published);
        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        await SettleAsync(cut);
        await cut.Find(".sqlite-new-database-option").ClickAsync(new());
        await SettleAsync(cut);

        await CreateTasksAsync(cut);

        published.Last().Id.Should().Be(_newId);
        navigation.Uri.Should().EndWith($"/p/sqlite/overview?connectionId={_newId}");
        cut.Find(".sqlite-selected-connection").TextContent.Should().Be("sqlite-tasks");
        cut.FindAll(".sqlite-newdb-dialog").Should().BeEmpty();
        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        await SettleAsync(cut);
        cut.FindAll(".sqlite-connection-option").Select(o => o.TextContent).Should().Contain("sqlite-tasks");
    }

    [Fact]
    public async Task Creating_from_another_page_opens_the_new_connections_Overview()
    {
        ArrangeCreation();
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(Array.Empty<ConnectionInfo>());
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/p/sqlite/tables");
        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SqliteConnectionPicker>(0);
            builder.CloseComponent();
        });
        await cut.Find(".sqlite-new-database").ClickAsync(new());

        await CreateTasksAsync(cut);

        navigation.Uri.Should().EndWith($"/p/sqlite/overview?connectionId={_newId}");
    }
}
