using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;

namespace SbConsole.Core.Tests;

/// <summary>In-memory SQLite database that lives as long as the open connection.</summary>
public sealed class TestDb : IDbContextFactory<SbcDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<SbcDbContext> _options;

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<SbcDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public SbcDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
