using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Core.Data;

namespace SbConsole.Web.Tests;

/// <summary>
/// SBC_DEMO_CONNECTIONS is wired into the real Program.cs startup after the database is migrated:
/// entries are created (via the normal create path, audited as "system"), a second start creates
/// nothing new, and a broken file never stops the app from starting.
/// </summary>
[Collection("ProgramFactory")]
public sealed class DemoConnectionsStartupTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sbc-demo-test-{Guid.NewGuid()}.db");
    private readonly string _demoFile = Path.Combine(Path.GetTempPath(), $"sbc-demo-test-{Guid.NewGuid()}.json");
    private readonly Dictionary<string, string?> _original = [];

    private static readonly string[] Vars =
        ["SBC_DB_PATH", "SBC_DATA_KEY", "SBC_ADMIN_PASSWORD", "SBC_API_KEY", "SBC_DEMO_CONNECTIONS"];

    public DemoConnectionsStartupTests()
    {
        foreach (var v in Vars)
        {
            _original[v] = Environment.GetEnvironmentVariable(v);
        }

        Environment.SetEnvironmentVariable("SBC_DB_PATH", _dbPath);
        Environment.SetEnvironmentVariable("SBC_DATA_KEY", Convert.ToBase64String(new byte[32]));
        Environment.SetEnvironmentVariable("SBC_ADMIN_PASSWORD", "pw");
        Environment.SetEnvironmentVariable("SBC_API_KEY", "test-api-key");
        Environment.SetEnvironmentVariable("SBC_DEMO_CONNECTIONS", _demoFile);
    }

    private static async Task<List<(string Name, string Tags)>> ConnectionsAsync(WebApplicationFactory<Program> factory)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<SbcDbContext>>().CreateDbContextAsync();
        return (await db.Connections.OrderBy(c => c.Name).Select(c => new { c.Name, c.TagsCsv }).ToListAsync())
            .Select(c => (c.Name, c.TagsCsv)).ToList();
    }

    [Fact]
    public async Task Startup_creates_missing_demo_connections_once_and_audits_them_as_system()
    {
        await File.WriteAllTextAsync(_demoFile, """
            [
              { "name": "sqlite-reports", "kind": "sqlite", "secret": "Path=/sqlite/reports.db;Mode=ReadWrite", "tags": ["prod"] },
              { "name": "ghost", "kind": "not-a-plugin", "secret": "x" }
            ]
            """);

        await using (var first = new WebApplicationFactory<Program>())
        {
            (await ConnectionsAsync(first)).Should().Equal([("sqlite-reports", "prod")]);
        }

        await using var second = new WebApplicationFactory<Program>();
        (await ConnectionsAsync(second)).Should().HaveCount(1);
        await using var db = await second.Services.GetRequiredService<IDbContextFactory<SbcDbContext>>().CreateDbContextAsync();
        var audits = await db.AuditEntries.Where(a => a.Action == "connection.create").ToListAsync();
        audits.Should().ContainSingle().Which.Actor.Should().Be("system");
    }

    [Fact]
    public async Task An_invalid_demo_file_does_not_stop_startup()
    {
        await File.WriteAllTextAsync(_demoFile, "{ definitely not json");

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/v1/health")).IsSuccessStatusCode.Should().BeTrue();
        (await ConnectionsAsync(factory)).Should().BeEmpty();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _demoFile })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        foreach (var (k, v) in _original)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
    }
}
