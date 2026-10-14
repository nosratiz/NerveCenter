using FluentAssertions;
using Microsoft.Data.Sqlite;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Design spec §11.6: "Recover into new file" -- a best-effort salvage into BackupDir.</summary>
public sealed class SqliteOperationsSalvageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 40, 51, TimeSpan.Zero);

    private readonly TempDatabase _db = new("""
        CREATE TABLE tenants(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE events(id INTEGER PRIMARY KEY, tenant_id INTEGER REFERENCES tenants(id), body BLOB, total REAL,
            label TEXT GENERATED ALWAYS AS ('#' || id) VIRTUAL);
        CREATE INDEX idx_events_tenant ON events(tenant_id);
        CREATE VIEW v_events AS SELECT e.id, t.name FROM events e JOIN tenants t ON t.id = e.tenant_id;
        CREATE TRIGGER trg_tenants_ai AFTER INSERT ON tenants BEGIN SELECT 1; END;
        INSERT INTO tenants VALUES (1, 'acme'), (2, 'globex');
        INSERT INTO events(id, tenant_id, body, total) VALUES (1, 1, x'00ff', 1.5), (2, 1, NULL, 2.25), (3, 2, 'text', NULL);
        """, fileName: "reports.db");

    private string Secret(SqliteAccessMode mode = SqliteAccessMode.ReadWrite, bool backups = true) =>
        _db.Secret(mode: mode, backupDir: backups ? _db.BackupDir : null);

    private SqliteConnection OpenCopy(string fileName)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_db.BackupDir, fileName)};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task A_healthy_file_is_copied_completely_into_a_new_recovered_file()
    {
        var before = File.ReadAllBytes(_db.Path);

        var outcome = await _db.Operations().SalvageAsync(Secret(), Now);

        outcome.FileName.Should().Be("reports-recovered-20261013-094051.db");
        outcome.TablesCopied.Should().Be(2);
        outcome.RowsCopied.Should().Be(5);
        outcome.RowsSkipped.Should().Be(0);
        outcome.OtherObjectsCopied.Should().Be(3); // index, view, trigger
        outcome.Failures.Should().BeEmpty();
        File.ReadAllBytes(_db.Path).Should().Equal(before, "the live file is never modified");

        using var copy = OpenCopy(outcome.FileName);
        SqliteSql.Long(copy, "SELECT COUNT(*) FROM v_events").Should().Be(3);
        SqliteSql.Text(copy, "SELECT label FROM events WHERE id = 3").Should().Be("#3");
        SqliteSql.Text(copy, "SELECT hex(body) FROM events WHERE id = 1").Should().Be("00FF");
        SqliteSql.Text(copy, "SELECT typeof(body) || ',' || typeof(total) FROM events WHERE id = 3").Should().Be("text,null");
        SqliteSql.Text(copy, "PRAGMA integrity_check").Should().Be("ok");
        SqliteSql.Long(copy, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('idx_events_tenant', 'trg_tenants_ai')").Should().Be(2);
    }

    [Fact]
    public async Task A_second_recover_in_the_same_second_gets_a_free_name()
    {
        var ops = _db.Operations();

        var first = await ops.SalvageAsync(Secret(), Now);
        var second = await ops.SalvageAsync(Secret(), Now);

        second.FileName.Should().Be("reports-recovered-20261013-094051-2.db");
        first.FileName.Should().NotBe(second.FileName);
    }

    [Fact]
    public async Task Rows_that_fail_to_insert_are_skipped_and_counted()
    {
        _db.Execute("""
            CREATE TABLE checked(x INTEGER CHECK (x > 0));
            PRAGMA ignore_check_constraints = ON;
            INSERT INTO checked VALUES (1), (-1), (2);
            """);

        var outcome = await _db.Operations().SalvageAsync(Secret(), Now);

        outcome.TablesCopied.Should().Be(3);
        outcome.RowsCopied.Should().Be(7);
        outcome.RowsSkipped.Should().Be(1);
        using var copy = OpenCopy(outcome.FileName);
        SqliteSql.Long(copy, "SELECT COUNT(*) FROM checked").Should().Be(2);
    }

    [Fact]
    public async Task A_table_whose_ddl_fails_and_its_index_are_reported_and_the_rest_is_copied()
    {
        // A schema the target can't recreate: the collation exists nowhere but in the file's text.
        _db.Execute("""
            CREATE TABLE weird(x TEXT);
            INSERT INTO weird VALUES ('a');
            CREATE INDEX idx_weird ON weird(x);
            PRAGMA writable_schema = ON;
            UPDATE sqlite_schema SET sql = 'CREATE TABLE weird(x TEXT COLLATE no_such_collation)' WHERE name = 'weird';
            PRAGMA writable_schema = OFF;
            """);

        var outcome = await _db.Operations().SalvageAsync(Secret(), Now);

        outcome.TablesCopied.Should().Be(2);
        outcome.TablesFailed.Should().Be(1);
        outcome.Failures.Select(f => (f.Type, f.Name)).Should().Equal(("table", "weird"), ("index", "idx_weird"));
        outcome.Failures[0].Reason.Should().NotBeNullOrWhiteSpace();
        outcome.RowsCopied.Should().Be(5);
    }

    [Fact]
    public async Task A_table_with_a_corrupt_page_stops_part_way_and_the_other_tables_still_copy()
    {
        _db.Execute("CREATE TABLE broken(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO broken VALUES (1, 'x'), (2, 'y');");
        long root;
        long pageSize;
        using (var c = _db.OpenWriter())
        {
            root = SqliteSql.Long(c, "SELECT rootpage FROM sqlite_schema WHERE name = 'broken'");
            pageSize = SqliteSql.Long(c, "PRAGMA page_size");
        }

        SqliteConnection.ClearAllPools();
        using (var file = new FileStream(_db.Path, FileMode.Open, FileAccess.Write))
        {
            file.Position = (root - 1) * pageSize;
            file.Write(Enumerable.Repeat((byte)0xEE, 64).ToArray());
        }

        var outcome = await _db.Operations().SalvageAsync(Secret(), Now);

        outcome.TablesCopied.Should().Be(2);
        outcome.Failures.Should().ContainSingle().Which.Should().Match<SalvageFailure>(f =>
            f.Type == "table" && f.Name == "broken" && f.Reason.StartsWith("stopped after 0 rows — "));
        outcome.RowsCopied.Should().Be(5);
        using var copy = OpenCopy(outcome.FileName);
        SqliteSql.Long(copy, "SELECT COUNT(*) FROM broken").Should().Be(0);
        SqliteSql.Long(copy, "SELECT COUNT(*) FROM tenants").Should().Be(2);
    }

    [Fact]
    public async Task A_read_only_connection_is_refused_and_nothing_is_created()
    {
        var act = () => _db.Operations().SalvageAsync(Secret(SqliteAccessMode.ReadOnly), Now);

        await act.Should().ThrowAsync<SqliteReadOnlyConnectionException>();
        Directory.GetFiles(_db.BackupDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_backup_directory_it_is_refused()
    {
        var act = () => _db.Operations().SalvageAsync(Secret(backups: false), Now);

        (await act.Should().ThrowAsync<SqliteBackupException>()).Which.Message.Should().Be(SqliteOperations.BackupsNotEnabled);
    }

    [Fact]
    public async Task A_cancelled_salvage_leaves_no_partial_file()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _db.Operations().SalvageAsync(Secret(), Now, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(_db.BackupDir).Should().BeEmpty();
    }

    public void Dispose() => _db.Dispose();
}
