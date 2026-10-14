using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

/// <summary>On Blazor Server NavigationManager.Uri only changes once the client round-trips; this fake models that.</summary>
public class LaggingNavigationManager : NavigationManager
{
    public LaggingNavigationManager() => Initialize("http://localhost/", "http://localhost/");

    public List<string> Requested { get; } = [];

    public void Start(string uri) => Uri = ToAbsoluteUri(uri).ToString();

    protected override void NavigateToCore(string uri, NavigationOptions options) => Requested.Add(ToAbsoluteUri(uri).ToString());
}

public class BrowseLaggingNavigationTests : PgPageTestBase
{
    private readonly LaggingNavigationManager _nav = new();

    public BrowseLaggingNavigationTests()
    {
        Services.AddSingleton<NavigationManager>(_nav);
        Operations.ListDatabasesAsync(Secret, Arg.Any<CancellationToken>()).Returns(["app", "analytics"]);
        foreach (var db in new[] { "app", "analytics" })
        {
            Operations.ListSchemaAsync(Secret, db, Arg.Any<CancellationToken>())
                .Returns(new PgSchemaTree(db, [new PgSchemaNode("public", [new PgObjectNode(1, "public", "t", PgObjectKind.Table, 1)], [], [], [])]));
        }
    }

    [Fact]
    public void A_database_change_is_written_to_the_url_even_while_navigation_uri_lags()
    {
        _nav.Start($"/p/postgres/browse?connectionId={ConnectionId}&db=app&oid=1");
        var cut = RenderWithPopovers(b =>
        {
            b.OpenComponent<SbConsole.Plugins.Postgres.Pages.Browse>(0);
            b.CloseComponent();
        });
        cut.WaitForElement(".pg-tree-item");
        _nav.Requested.Clear();

        cut.Find(".pg-database-picker").MouseDown();
        cut.WaitForElement(".mud-popover-open .mud-list-item");
        cut.FindAll(".mud-popover-open .mud-list-item").First(i => i.TextContent.Trim() == "analytics").Click();

        cut.WaitForAssertion(() => cut.FindAll(".pg-tree-item").Should().HaveCount(1));
        _nav.Requested.Should().NotBeEmpty();
        _nav.Requested.Should().OnlyContain(u => u.Contains("db=analytics") && !u.Contains("db=app") && !u.Contains("oid="));
        _nav.Requested.Last().Should().Contain($"connectionId={ConnectionId}");
    }
}
