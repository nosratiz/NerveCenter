using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Core.Data;

namespace SbConsole.Core.Tests.Data;

/// <summary>
/// Regression coverage for the AuditEntryAtStoredAsUtcTicks migration: it must backfill
/// pre-existing "At" values (old TEXT DateTimeOffset format) into the new UTC-ticks INTEGER
/// column, not just change the column's declared type. This exercises a real file-backed SQLite
/// database and the actual migrations pipeline -- TestDb's EnsureCreated() never applies
/// migrations, so it would not catch this bug.
/// </summary>
public class AuditEntryMigrationTests
{
    private const string InitialCreateMigrationId = "20260909204731_InitialCreate";

    [Fact]
    public async Task Migrating_an_existing_database_preserves_audit_timestamps()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sbc-migration-test-{Guid.NewGuid()}.db");
        var connectionString = $"Data Source={dbPath}";
        try
        {
            // 1. Bring the database up to just before the ticks-conversion migration, simulating a
            //    real pre-existing database created by an older version of the app.
            await using (var db = new SbcDbContext(new DbContextOptionsBuilder<SbcDbContext>().UseSqlite(connectionString).Options))
            {
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrator.MigrateAsync(InitialCreateMigrationId);
            }

            // 2. Insert a row directly via SQL using the old TEXT DateTimeOffset format, exactly as
            //    EF Core's SQLite provider would have written it before this migration existed.
            var originalInstant = new DateTimeOffset(2026, 9, 9, 20, 50, 0, TimeSpan.Zero);
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO AuditEntries (At, Actor, Action, Target, Risk, Succeeded) " +
                    "VALUES ($at, 'admin', 'auth.login', '-', 'Safe', 1)";
                command.Parameters.AddWithValue("$at", originalInstant.ToString("yyyy-MM-dd HH:mm:sszzz"));
                await command.ExecuteNonQueryAsync();
            }

            // 3. Apply the rest of the migrations (including the fixed backfill) and confirm the
            //    pre-existing row still reads back as the same instant through the real DbContext.
            await using (var db = new SbcDbContext(new DbContextOptionsBuilder<SbcDbContext>().UseSqlite(connectionString).Options))
            {
                await db.Database.MigrateAsync();

                var entry = await db.AuditEntries.SingleAsync();
                entry.At.Should().Be(originalInstant);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    /// <summary>
    /// Every real audit row written via <c>TimeProvider.GetUtcNow()</c> carries 7 fractional-second
    /// digits (e.g. "2026-03-15 13:45:30.1234567+00:00"), unlike the zero-fraction timestamp used
    /// above. The backfill's julianday()-based conversion is documented as accurate only to the
    /// millisecond, so this confirms a fractional timestamp still survives the migration -- to
    /// millisecond precision, with the sub-millisecond remainder intentionally and acceptably lost.
    /// </summary>
    [Fact]
    public async Task Migrating_an_existing_database_preserves_audit_timestamps_with_fractional_seconds()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sbc-migration-test-{Guid.NewGuid()}.db");
        var connectionString = $"Data Source={dbPath}";
        try
        {
            await using (var db = new SbcDbContext(new DbContextOptionsBuilder<SbcDbContext>().UseSqlite(connectionString).Options))
            {
                var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrator.MigrateAsync(InitialCreateMigrationId);
            }

            const string originalText = "2026-03-15 13:45:30.1234567+00:00";
            var originalInstant = DateTimeOffset.Parse(originalText);
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO AuditEntries (At, Actor, Action, Target, Risk, Succeeded) " +
                    "VALUES ($at, 'admin', 'auth.login', '-', 'Safe', 1)";
                command.Parameters.AddWithValue("$at", originalText);
                await command.ExecuteNonQueryAsync();
            }

            await using (var db = new SbcDbContext(new DbContextOptionsBuilder<SbcDbContext>().UseSqlite(connectionString).Options))
            {
                await db.Database.MigrateAsync();

                var entry = await db.AuditEntries.SingleAsync();
                entry.At.Should().BeCloseTo(originalInstant, TimeSpan.FromMilliseconds(2));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
