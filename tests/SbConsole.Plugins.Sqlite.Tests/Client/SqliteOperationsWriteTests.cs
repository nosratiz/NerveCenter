using FluentAssertions;
using Microsoft.Data.Sqlite;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Design spec §11.4: dry run, run in transaction, WAL checkpoint.</summary>
public sealed class SqliteOperationsWriteTests : IDisposable
{
    private const string Forever = "WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x) SELECT count(*) FROM x";

    // Two cascading children: a rowid table and a WITHOUT ROWID one (spec §11 verification note).
    private readonly TempDatabase _db = new("""
        CREATE TABLE tenants(id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE sessions(id INTEGER PRIMARY KEY, tenant_id INTEGER REFERENCES tenants(id) ON DELETE CASCADE);
        CREATE TABLE tags(tenant_id INTEGER REFERENCES tenants(id) ON DELETE CASCADE, tag TEXT, PRIMARY KEY (tenant_id, tag)) WITHOUT ROWID;
        INSERT INTO tenants VALUES (1, 'acme'), (2, 'globex'), (3, 'initech');
        INSERT INTO sessions VALUES (10, 1), (11, 1), (12, 1), (20, 2);
        INSERT INTO tags VALUES (1, 'a'), (1, 'b'), (2, 'a');
        """, wal: true);

    private string WriteSecret(int statementTimeoutS = 30) => _db.Secret(statementTimeoutS, mode: SqliteAccessMode.ReadWrite);

    private long Count(string table)
    {
        using var check = _db.OpenWriter();
        return SqliteSql.Long(check, $"SELECT COUNT(*) FROM {table}");
    }

    [Fact]
    public async Task Dry_run_reports_direct_and_cascaded_rows_and_changes_nothing()
    {
        var result = await _db.Operations().DryRunWriteAsync(WriteSecret(), "DELETE FROM tenants WHERE id = 1");

        result.Committed.Should().BeFalse();
        result.Statements.Should().ContainSingle();
        result.Statements[0].Keyword.Should().Be("DELETE");
        result.Statements[0].DirectRows.Should().Be(1);
        result.Statements[0].CascadedRows.Should().Be(5); // 3 sessions + 2 tags
        result.DirectRows.Should().Be(1);
        result.CascadedRows.Should().Be(5);
        result.After.Should().BeNull();
        Count("tenants").Should().Be(3);
        Count("sessions").Should().Be(4);
        Count("tags").Should().Be(3);
    }

    [Fact]
    public async Task Dry_run_counts_each_statement_separately_and_ddl_changes_no_rows()
    {
        var result = await _db.Operations().DryRunWriteAsync(WriteSecret(), """
            UPDATE tenants SET name = upper(name);
            CREATE TABLE scratch(x);
            INSERT INTO tenants VALUES (4, 'umbrella');
            WITH doomed AS (SELECT id FROM tenants WHERE id = 2) DELETE FROM tenants WHERE id IN (SELECT id FROM doomed);
            SELECT * FROM tenants
            """);

        result.Statements.Select(s => (s.Keyword, s.DirectRows, s.CascadedRows)).Should().Equal(
            ("UPDATE", 3L, 0L), ("CREATE", 0L, 0L), ("INSERT", 1L, 0L), ("WITH", 1L, 2L), ("SELECT", 0L, 0L));
        using var check = _db.OpenWriter();
        SqliteSql.Long(check, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'scratch'").Should().Be(0);
        SqliteSql.Text(check, "SELECT name FROM tenants WHERE id = 1").Should().Be("acme");
    }

    [Fact]
    public async Task Dry_run_counts_rows_written_by_triggers_as_cascaded()
    {
        _db.Execute("""
            CREATE TABLE log(msg TEXT);
            CREATE TRIGGER tenants_log AFTER UPDATE ON tenants BEGIN INSERT INTO log VALUES (new.name); INSERT INTO log VALUES ('again'); END;
            """);

        var result = await _db.Operations().DryRunWriteAsync(WriteSecret(), "UPDATE tenants SET name = 'x' WHERE id = 3");

        result.Statements[0].DirectRows.Should().Be(1);
        result.Statements[0].CascadedRows.Should().Be(2);
        Count("log").Should().Be(0);
    }

    [Fact]
    public async Task Run_write_commits_and_reports_the_file_state_after()
    {
        var result = await _db.Operations().RunWriteAsync(WriteSecret(), "DELETE FROM tenants WHERE id = 1; UPDATE tenants SET name = 'g' WHERE id = 2");

        result.Committed.Should().BeTrue();
        result.Statements.Select(s => (s.DirectRows, s.CascadedRows)).Should().Equal((1L, 5L), (1L, 0L));
        result.After.Should().NotBeNull();
        result.After!.WalBytes.Should().BeGreaterThan(0);
        result.After.FreePagePercent.Should().BeInRange(0, 100);
        Count("tenants").Should().Be(2);
        Count("sessions").Should().Be(1);
        Count("tags").Should().Be(1);
    }

    [Fact]
    public async Task Run_write_rolls_back_everything_when_a_later_statement_fails()
    {
        var act = () => _db.Operations().RunWriteAsync(WriteSecret(), "DELETE FROM tenants WHERE id = 1; INSERT INTO tenants VALUES (2, 'duplicate')");

        (await act.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(19); // SQLITE_CONSTRAINT
        Count("tenants").Should().Be(3);
        Count("sessions").Should().Be(4);
    }

    [Fact]
    public async Task A_write_over_the_statement_timeout_is_interrupted_and_rolled_back()
    {
        var act = () => _db.Operations().RunWriteAsync(WriteSecret(statementTimeoutS: 1),
            $"INSERT INTO tenants VALUES (9, 'early'); INSERT INTO tenants(name) {Forever}");

        await act.Should().ThrowAsync<SqliteStatementTimeoutException>();
        Count("tenants").Should().Be(3);
    }

    [Fact]
    public async Task The_connection_is_released_after_a_failed_write()
    {
        var act = () => _db.Operations().RunWriteAsync(WriteSecret(), "DELETE FROM tenants; INSERT INTO nope VALUES (1)");
        await act.Should().ThrowAsync<SqliteException>();

        // Nobody still holds the write lock.
        using var writer = _db.OpenWriter();
        TempDatabase.Run(writer, "BEGIN IMMEDIATE; INSERT INTO tenants VALUES (7, 'x'); COMMIT;");
        Count("tenants").Should().Be(4);
    }

    [Fact]
    public async Task Write_operations_refuse_a_read_only_connection()
    {
        var operations = _db.Operations();

        var dry = () => operations.DryRunWriteAsync(_db.Secret(), "DELETE FROM tenants");
        var run = () => operations.RunWriteAsync(_db.Secret(), "DELETE FROM tenants");
        var checkpoint = () => operations.CheckpointWalAsync(_db.Secret());

        await dry.Should().ThrowAsync<SqliteReadOnlyConnectionException>();
        await run.Should().ThrowAsync<SqliteReadOnlyConnectionException>();
        await checkpoint.Should().ThrowAsync<SqliteReadOnlyConnectionException>();
        Count("tenants").Should().Be(3);
    }

    [Theory]
    [InlineData("DELETE FROM tenants WHERE id = 1; COMMIT")]
    [InlineData("DELETE FROM tenants WHERE id = 1; VACUUM")]
    [InlineData("DELETE FROM tenants WHERE id = 1; ATTACH 'x.db' AS x")]
    public async Task A_refused_statement_stops_the_whole_script_before_anything_runs(string sql)
    {
        var act = () => _db.Operations().RunWriteAsync(WriteSecret(), sql);

        await act.Should().ThrowAsync<SqliteScriptRefusedException>();
        Count("tenants").Should().Be(3);
    }

    [Fact]
    public void The_write_connection_is_attach_limited_too()
    {
        var copy = System.IO.Path.Combine(_db.Directory, "copy.db");

        var act = () => _db.Operations().Run(WriteSecret(), SqliteOpenOptions.UserWrite, c =>
        {
            SqliteSql.Execute(c, "VACUUM INTO @p", ("@p", copy));
            return 0;
        }, CancellationToken.None);

        act.Should().Throw<SqliteException>();
        File.Exists(copy).Should().BeFalse();
    }

    [Fact]
    public async Task Checkpoint_truncates_the_wal()
    {
        // An idle second connection keeps the -wal file from being checkpointed and deleted on close.
        using var holder = _db.OpenWriter();
        SqliteSql.Long(holder, "SELECT COUNT(*) FROM tenants");
        await _db.Operations().RunWriteAsync(WriteSecret(), "UPDATE tenants SET name = name || '!'");

        var result = await _db.Operations().CheckpointWalAsync(WriteSecret());

        result.Busy.Should().BeFalse();
        result.LogFrames.Should().Be(result.CheckpointedFrames);
        new FileInfo(_db.Path + "-wal").Length.Should().Be(0);
    }

    public void Dispose() => _db.Dispose();
}
