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

    [Fact]
    public void Postgres_demo_secrets_round_trip_through_the_connection_settings_format()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DemoFile()));

        var entries = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "postgres").ToList();
        entries.Select(e => e.GetProperty("name").GetString()).Should().Equal("pg-local", "pg-local-ro");

        foreach (var entry in entries)
        {
            var secret = entry.GetProperty("secret").GetString()!;
            var s = PostgresConnectionSettings.Parse(secret);
            s.ToSecretString().Should().Be(secret);
            s.Host.Should().Be("postgres", "the app container reaches the server on the compose network");
            s.Port.Should().Be(5432);
            s.Database.Should().Be("app");
            s.Username.Should().Be("sbconsole");
            s.Password.Should().Be("sbconsole");
            s.SslMode.Should().Be("Prefer");
            s.ConnectTimeoutS.Should().Be(5);
            s.StatementTimeoutS.Should().Be(30);
        }

        var rw = entries[0];
        PostgresConnectionSettings.Parse(rw.GetProperty("secret").GetString()!).Mode.Should().Be(PgAccessMode.ReadWrite);
        rw.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("prod");

        var ro = entries[1];
        PostgresConnectionSettings.Parse(ro.GetProperty("secret").GetString()!).Mode.Should().Be(PgAccessMode.ReadOnly);
        ro.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("demo");
    }
}
