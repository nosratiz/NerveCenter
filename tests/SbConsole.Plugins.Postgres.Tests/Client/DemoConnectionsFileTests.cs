using System.Text.Json;
using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

/// <summary>
/// docker/sqlite/demo-connections.json is fed to SBC_DEMO_CONNECTIONS by the compose stack (one file
/// for every plugin). The postgres entries' secrets must be exactly what the PostgreSQL connection
/// form would have produced, so a format change in PostgresConnectionSettings can't silently leave
/// the demo file behind.
/// </summary>
public class DemoConnectionsFileTests
{
    private static string DemoFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SbConsole.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return Path.Combine(dir!.FullName, "docker", "sqlite", "demo-connections.json");
    }

    public static TheoryData<string, string, string, string, PgAccessMode, string> Expected => new()
    {
        // name, host, database, user/password, mode, tag
        { "pg-local", "postgres", "app", "sbconsole", PgAccessMode.ReadWrite, "prod" },
        { "pg-local-ro", "postgres", "app", "sbconsole", PgAccessMode.ReadOnly, "demo" },
        // A non-superuser without pg_monitor (created by docker/postgres/init/04-showcase.sql).
        { "pg-local-limited", "postgres", "app", "app_ro", PgAccessMode.ReadOnly, "demo" },
        // Never resolves (.invalid is reserved): shows the Overview's unreachable state.
        { "pg-offline", "pg-offline.invalid", "analytics", "sbconsole", PgAccessMode.ReadOnly, "staging" },
    };

    [Fact]
    public void The_demo_file_lists_the_postgres_connections_in_order()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DemoFile()));

        doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "postgres")
            .Select(e => e.GetProperty("name").GetString())
            .Should().Equal("pg-local", "pg-local-ro", "pg-local-limited", "pg-offline");
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public void Postgres_demo_secrets_round_trip_through_the_connection_settings_format(
        string name, string host, string database, string user, PgAccessMode mode, string tag)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DemoFile()));
        var entry = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
        entry.GetProperty("kind").GetString().Should().Be("postgres");

        var secret = entry.GetProperty("secret").GetString()!;
        var s = PostgresConnectionSettings.Parse(secret);
        s.ToSecretString().Should().Be(secret, "the secret must be exactly what the connection form produces");
        s.Host.Should().Be(host, "the app container reaches the server on the compose network");
        s.Port.Should().Be(5432);
        s.Database.Should().Be(database);
        s.Username.Should().Be(user);
        s.Password.Should().Be(user);
        s.SslMode.Should().Be("Prefer");
        s.ConnectTimeoutS.Should().Be(5);
        s.StatementTimeoutS.Should().Be(30);
        s.Mode.Should().Be(mode);
        entry.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal(tag);
    }
}
