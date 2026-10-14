using Microsoft.Extensions.DependencyInjection;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using SbConsole.Plugins.Postgres.Components;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

public class PgDatabasePickerTests : PgPageTestBase
{
    private readonly Guid _other = Guid.NewGuid();

    private IRenderedComponent<PgDatabasePicker> RenderPicker(Guid id, string? defaultDb, List<string> selected) =>
        Render<PgDatabasePicker>(p => p
            .Add(c => c.ConnectionId, id)
            .Add(c => c.Default, defaultDb)
            .Add(c => c.SelectedChanged, EventCallback.Factory.Create<string>(this, selected.Add)));

    private void Arrange(Guid id, params string[] names)
    {
        Connections.GetSecretAsync(id, Arg.Any<CancellationToken>()).Returns(Secret + id);
        Operations.ListDatabasesAsync(Secret + id, Arg.Any<CancellationToken>()).Returns(names);
    }

    [Fact]
    public void Query_db_selects_that_database_when_listed()
    {
        Arrange(ConnectionId, "app", "analytics");
        Services.GetService<NavigationManager>()!.NavigateTo("/p/postgres/browse?db=analytics");
        var selected = new List<string>();
        RenderPicker(ConnectionId, "app", selected);

        selected.Should().Equal("analytics");
    }

    [Fact]
    public void Unknown_query_db_falls_back_to_default()
    {
        Arrange(ConnectionId, "app", "analytics");
        Services.GetService<NavigationManager>()!.NavigateTo("/p/postgres/browse?db=nope");
        var selected = new List<string>();
        RenderPicker(ConnectionId, "analytics", selected);

        selected.Should().Equal("analytics");
    }

    [Fact]
    public void A_slow_older_response_cannot_overwrite_a_newer_connection_list()
    {
        var slow = new TaskCompletionSource<IReadOnlyList<string>>();
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret + "first");
        Operations.ListDatabasesAsync(Secret + "first", Arg.Any<CancellationToken>()).Returns(_ => slow.Task);
        Arrange(_other, "fresh");
        var selected = new List<string>();
        var cut = RenderPicker(ConnectionId, null, selected);

        cut.Render(p => p.Add(c => c.ConnectionId, _other));
        cut.WaitForAssertion(() => selected.Should().Equal("fresh"));
        slow.SetResult(["stale"]);
        Thread.Sleep(100);

        selected.Should().Equal("fresh");
        cut.Instance.Should().NotBeNull();
        cut.Markup.Should().NotContain("stale");
    }

    [Fact]
    public void A_failed_load_after_a_connection_change_shows_nothing_selected()
    {
        Arrange(ConnectionId, "app");
        var selected = new List<string>();
        var cut = RenderPicker(ConnectionId, null, selected);
        cut.FindAll(".pg-database-picker").Should().HaveCount(1);

        Connections.GetSecretAsync(_other, Arg.Any<CancellationToken>()).Returns((string?)null);
        cut.Render(p => p.Add(c => c.ConnectionId, _other));

        cut.FindAll(".pg-database-picker").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_load_after_a_connection_change_tells_the_parent_and_drops_db_from_the_url()
    {
        Arrange(ConnectionId, "app");
        Services.GetService<NavigationManager>()!.NavigateTo("/p/postgres/browse?db=app");
        var selected = new List<string>();
        var cut = RenderPicker(ConnectionId, null, selected);
        selected.Should().Equal("app");

        Connections.GetSecretAsync(_other, Arg.Any<CancellationToken>()).Returns((string?)null);
        cut.Render(p => p.Add(c => c.ConnectionId, _other));

        cut.WaitForAssertion(() => selected.Should().Equal("app", ""));
        Services.GetService<NavigationManager>()!.Uri.Should().NotContain("db=");
    }

    [Fact]
    public void An_empty_database_list_tells_the_parent_with_an_empty_string()
    {
        Arrange(ConnectionId);
        var selected = new List<string>();
        RenderPicker(ConnectionId, null, selected);

        selected.Should().Equal("");
    }
}
