using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests;

public class SqlitePluginTests
{
    [Fact]
    public void Identity_is_sqlite()
    {
        var plugin = new SqlitePlugin();

        plugin.Id.Should().Be("sqlite");
        plugin.ConnectionKind.Should().Be("sqlite");
        plugin.DisplayName.Should().Be("SQLite");
        plugin.Monogram.Should().Be("SQ");
    }

    [Fact]
    public void Nav_items_are_overview_then_tables()
    {
        new SqlitePlugin().NavItems.Select(n => n.Href).Should().Equal("/p/sqlite/overview", "/p/sqlite/tables");
    }

    [Fact]
    public void Contribution_counts_two_pages_and_the_two_audited_actions()
    {
        var contribution = new SqlitePlugin().Contribution;

        contribution.PageCount.Should().Be(2);
        contribution.ActionCount.Should().Be(2);
    }

    [Fact]
    public void Connection_summary_shows_the_file_name_and_mode_but_not_the_directory()
    {
        var secret = new SqliteConnectionSettings("/data/private/reports.db").ToSecretString();

        var summary = new SqlitePlugin().GetConnectionSummary(secret);

        summary.Should().Equal(new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-only" });
    }

    [Fact]
    public void Connection_summary_is_empty_for_an_unparsable_secret()
    {
        new SqlitePlugin().GetConnectionSummary("not a secret").Should().BeEmpty();
    }

    [Fact]
    public async Task Test_connection_fails_closed_before_the_plugin_is_initialized()
    {
        var result = await new SqlitePlugin().TestConnectionAsync("Path=/data/reports.db");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("SQLite plugin is not initialized.");
    }
}
