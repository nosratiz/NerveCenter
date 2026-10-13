using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteOperationsOverviewTests
{
    [Fact]
    public async Task Overview_reads_file_and_pragma_values()
    {
        using var db = new TempDatabase("""
            CREATE TABLE t(id INTEGER PRIMARY KEY, payload BLOB);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000)
              INSERT INTO t SELECT i, randomblob(500) FROM n;
            DELETE FROM t;
            """);

        var overview = await db.Operations().GetOverviewAsync(db.Secret(busyTimeoutMs: 1234));

        overview.Path.Should().EndWith("test.db");
        overview.SqliteVersion.Should().StartWith("3.");
        overview.FileBytes.Should().Be(new FileInfo(db.Path).Length);
        overview.WalBytes.Should().BeNull();
        overview.PageSize.Should().Be(4096);
        overview.PageCount.Should().BeGreaterThan(0);
        overview.FreelistCount.Should().BeGreaterThan(0, "the DELETE leaves free pages behind");
        overview.FreePercent.Should().BeInRange(1, 100);
        overview.FreeBytes.Should().Be(overview.FreelistCount * overview.PageSize);
        overview.JournalMode.Should().Be("DELETE");
        overview.Synchronous.Should().BeOneOf("OFF", "NORMAL", "FULL", "EXTRA");
        overview.CacheBytes.Should().BeGreaterThan(0);
        overview.BusyTimeoutMs.Should().Be(1234);
    }

    [Fact]
    public async Task Overview_reports_wal_mode_and_the_wal_file_size()
    {
        using var db = new TempDatabase("CREATE TABLE t(x);", wal: true);
        using var writer = db.OpenWriter();
        TempDatabase.Run(writer, "PRAGMA wal_autocheckpoint = 0; INSERT INTO t VALUES (randomblob(10000));");

        var overview = await db.Operations().GetOverviewAsync(db.Secret());

        overview.JournalMode.Should().Be("WAL");
        overview.WalBytes.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(-2000L, 4096L, 2_048_000L, 500L)]
    [InlineData(100L, 4096L, 409_600L, 100L)]
    public void Cache_size_is_pages_when_positive_and_kib_when_negative(long cacheSize, long pageSize, long bytes, long pages)
    {
        var overview = new SqliteOverview("/x.db", "3", 0, null, DateTimeOffset.UnixEpoch, pageSize, 0, 0, "DELETE", "FULL", 1000, cacheSize, 5000);

        overview.CacheBytes.Should().Be(bytes);
        overview.CachePages.Should().Be(pages);
        overview.FreePercent.Should().Be(0);
    }

    [Theory]
    [InlineData(0L, "OFF")]
    [InlineData(1L, "NORMAL")]
    [InlineData(2L, "FULL")]
    [InlineData(3L, "EXTRA")]
    public void Synchronous_levels_get_their_pragma_names(long level, string name) =>
        SqliteOperations.SynchronousName(level).Should().Be(name);

    [Fact]
    public async Task The_bundled_sqlite_has_no_dbstat_so_tables_are_ranked_by_estimated_rows()
    {
        // Verified 2026-10-13: e_sqlite3 (SQLite 3.53.3) is built without SQLITE_ENABLE_DBSTAT_VTAB.
        // If this starts failing because sizes ARE available, the Overview's "Size by table" panel
        // lights up on its own -- update this test to assert byte ordering instead.
        using var db = new TempDatabase("""
            CREATE TABLE small(x);
            CREATE TABLE big(x);
            CREATE TABLE never_analyzed_until_now(x);
            CREATE INDEX ix_big ON big(x);
            INSERT INTO small VALUES (1), (2), (3);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 10) INSERT INTO big SELECT i FROM n;
            ANALYZE;
            """);

        var sizes = await db.Operations().GetTableSizesAsync(db.Secret());

        sizes.SizesAvailable.Should().BeFalse();
        sizes.TableCount.Should().Be(3);
        sizes.IndexCount.Should().Be(1);
        sizes.Entries.Select(e => (e.Name, e.EstimatedRows)).Should().Equal(
            ("big", 10L), ("small", 3L), ("never_analyzed_until_now", (long?)null));
        sizes.Entries.Should().OnlyContain(e => e.Type == SchemaObjectType.Table && e.Bytes == null);
    }

    [Fact]
    public async Task Table_sizes_never_list_sqlite_internal_objects()
    {
        using var db = new TempDatabase("CREATE TABLE u(x UNIQUE); INSERT INTO u VALUES (1); ANALYZE;");

        var sizes = await db.Operations().GetTableSizesAsync(db.Secret());

        sizes.Entries.Select(e => e.Name).Should().Equal("u");
        sizes.IndexCount.Should().Be(0, "sqlite_autoindex_* is internal");
    }
}
