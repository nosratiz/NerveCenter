using System.Text.Json;
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>
/// docker/sqlite/demo-connections.json is fed to SBC_DEMO_CONNECTIONS by the compose stack. Every
/// secret in it must be exactly what the SQLite connection form would have produced, so a format
/// change in SqliteConnectionSettings can't silently leave the demo file behind.
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
    public void Every_demo_secret_round_trips_through_the_connection_settings_format()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DemoFile()));

        // The one file holds every plugin's demo connections; this test owns only the sqlite ones.
        var entries = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "sqlite").ToList();
        entries.Select(e => e.GetProperty("name").GetString())
            .Should().Equal("sqlite-reports", "sqlite-shop", "sqlite-corrupt", "sqlite-reference", "sqlite-new");
        foreach (var entry in entries)
        {
            entry.GetProperty("kind").GetString().Should().Be("sqlite");
            var secret = entry.GetProperty("secret").GetString()!;
            SqliteConnectionSettings.Parse(secret).ToSecretString().Should().Be(secret);
        }

        SqliteConnectionSettings.Parse(entries[3].GetProperty("secret").GetString()!).Mode
            .Should().Be(SqliteAccessMode.ReadOnly, "sqlite-reference demonstrates the read-only UI state");
    }
}
