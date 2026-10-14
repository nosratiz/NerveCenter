using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests;

public class PostgresPluginTests
{
    [Fact]
    public void Identity_is_postgres()
    {
        var plugin = new PostgresPlugin();

        plugin.Id.Should().Be("postgres");
        plugin.ConnectionKind.Should().Be("postgres");
        plugin.DisplayName.Should().Be("PostgreSQL");
        plugin.ConnectionKindDisplayName.Should().Be("PostgreSQL");
        plugin.Monogram.Should().Be("PG");
        plugin.Version.Should().Be("0.1.0");
        plugin.Contribution.PageCount.Should().Be(5);
        plugin.Contribution.ActionCount.Should().Be(11);
    }

    [Fact]
    public void Nav_items_are_in_order()
    {
        var nav = new PostgresPlugin().NavItems;

        nav.Select(n => n.Title).Should().Equal("Overview", "Browse", "Query", "Activity", "Maintenance");
        nav.Select(n => n.Href).Should().Equal(
            "/p/postgres/overview", "/p/postgres/browse", "/p/postgres/query", "/p/postgres/activity", "/p/postgres/maintenance");
    }

    [Fact]
    public void Connection_summary_never_contains_username_or_password()
    {
        var secret = new PostgresConnectionSettings("db.local", 5433, "shop", "svc_user", "s3cret").ToSecretString();

        var summary = new PostgresPlugin().GetConnectionSummary(secret);

        summary.Should().Equal(new Dictionary<string, string>
        {
            ["Server"] = "db.local:5433", ["Database"] = "shop", ["Mode"] = "read-only",
        });
        string.Concat(summary.Values).Should().NotContain("svc_user").And.NotContain("s3cret");
    }

    [Fact]
    public void Connection_summary_is_empty_on_garbage()
    {
        new PostgresPlugin().GetConnectionSummary("not a secret ;;; ").Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnection_fails_closed_before_initialize()
    {
        var result = await new PostgresPlugin().TestConnectionAsync("Host=h");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("PostgreSQL plugin is not initialized.");
    }
}
