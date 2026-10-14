using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Design spec §11.3: the read-only query runner, EXPLAIN and query CSV export.</summary>
public sealed class SqliteOperationsQueryTests : IDisposable
{
    private readonly TempDatabase _db = new("""
        CREATE TABLE events(id INTEGER PRIMARY KEY, name TEXT, ts INTEGER, payload BLOB, score REAL);
        INSERT INTO events VALUES
          (1, 'page.view', 1759052466780, NULL, 1.5),
          (2, 'page.view', 1759052468112, x'00010203', NULL),
          (3, 'export.csv', NULL, NULL, NULL);
        CREATE INDEX idx_events_name ON events(name);
        CREATE TABLE numbers(n INTEGER);
        WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x LIMIT 250) INSERT INTO numbers SELECT i FROM x;
        """);

    [Fact]
    public async Task Runs_a_select_with_columns_declared_types_and_rendered_cells()
    {
        var result = await _db.Operations().RunQueryAsync(_db.Secret(), "SELECT id, name, payload, score, id * 2 AS twice FROM events ORDER BY id", 100);

        result.Columns.Select(c => (c.Name, c.DeclaredType)).Should().Equal(
            ("id", "INTEGER"), ("name", "TEXT"), ("payload", "BLOB"), ("score", "REAL"), ("twice", null));
        result.Rows.Should().HaveCount(3);
        result.Rows[1][2].Should().Be(new CellValue(CellKind.Blob, "BLOB · 4 bytes"));
        result.Rows[0][3].Should().Be(new CellValue(CellKind.Real, "1.5"));
        result.Rows[2][1].Should().Be(new CellValue(CellKind.Text, "export.csv"));
        result.TotalRows.Should().Be(3);
        result.TotalCapped.Should().BeFalse();
        result.StatementCount.Should().Be(1);
        result.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task Reads_up_to_the_limit_but_keeps_counting()
    {
        var result = await _db.Operations().RunQueryAsync(_db.Secret(), "SELECT n FROM numbers ORDER BY n", 100);

        result.Rows.Should().HaveCount(100);
        result.Rows[^1][0].Display.Should().Be("100");
        result.TotalRows.Should().Be(250);
        result.TotalCapped.Should().BeFalse();
    }

    [Fact]
    public async Task Counting_stops_at_the_cap()
    {
        const string sql = "WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x LIMIT 100005) SELECT i FROM x";

        var result = await _db.Operations().RunQueryAsync(_db.Secret(), sql, 10);

        result.Rows.Should().HaveCount(10);
        result.TotalRows.Should().Be(QueryResult.CountCap);
        result.TotalCapped.Should().BeTrue();
    }

    [Fact]
    public async Task Exactly_the_cap_is_not_capped()
    {
        const string sql = "WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x LIMIT 100000) SELECT i FROM x";

        var result = await _db.Operations().RunQueryAsync(_db.Secret(), sql, 10);

        result.TotalRows.Should().Be(QueryResult.CountCap);
        result.TotalCapped.Should().BeFalse();
    }

    [Fact]
    public async Task Shows_the_last_statement_that_returned_columns()
    {
        var result = await _db.Operations().RunQueryAsync(_db.Secret(),
            "SELECT name FROM events; SELECT COUNT(*) AS n FROM numbers; CREATE TEMP TABLE scratch(x)", 100);

        result.Columns.Should().ContainSingle().Which.Name.Should().Be("n");
        result.Rows.Should().ContainSingle().Which[0].Display.Should().Be("250");
        result.StatementCount.Should().Be(3);
        result.ResultStatement.Should().Be(2);
    }

    [Fact]
    public async Task A_script_without_a_result_set_returns_no_columns()
    {
        var result = await _db.Operations().RunQueryAsync(_db.Secret(), "CREATE TEMP TABLE scratch(x)", 100);

        result.HasResultSet.Should().BeFalse();
        result.ResultStatement.Should().BeNull();
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Hints_unix_ms_for_integer_columns_whose_values_all_look_like_it()
    {
        var result = await _db.Operations().RunQueryAsync(_db.Secret(), "SELECT id, ts FROM events", 100);

        result.Columns.Select(c => c.UnixMsHint).Should().Equal(false, true);
    }

    [Fact]
    public async Task No_unix_ms_hint_when_any_value_is_out_of_range_or_not_an_integer()
    {
        var result = await _db.Operations().RunQueryAsync(_db.Secret(), "SELECT 1759052466780 AS a UNION ALL SELECT 5", 100);
        var text = await _db.Operations().RunQueryAsync(_db.Secret(), "SELECT 1759052466780 AS a UNION ALL SELECT 'x'", 100);
        var nulls = await _db.Operations().RunQueryAsync(_db.Secret(), "SELECT NULL AS a", 100);

        result.Columns[0].UnixMsHint.Should().BeFalse();
        text.Columns[0].UnixMsHint.Should().BeFalse();
        nulls.Columns[0].UnixMsHint.Should().BeFalse();
    }

    [Fact]
    public async Task A_query_never_writes_even_on_a_read_write_connection()
    {
        var act = () => _db.Operations().RunQueryAsync(_db.Secret(mode: SqliteAccessMode.ReadWrite), "DELETE FROM events", 100);

        (await act.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(8);
        using var check = _db.OpenWriter();
        SqliteSql.Long(check, "SELECT COUNT(*) FROM events").Should().Be(3);
    }

    [Fact]
    public async Task A_query_cannot_attach_another_file()
    {
        // The splitter refuses it before it reaches the engine; the attach limit is the backstop (GuardTests).
        var act = () => _db.Operations().RunQueryAsync(_db.Secret(), "ATTACH 'x.db' AS x", 100);

        await act.Should().ThrowAsync<SqliteScriptRefusedException>();
    }

    [Fact]
    public async Task A_refused_script_never_runs_any_statement()
    {
        var policy = TempDatabase.AllowingPolicy();
        var operations = new SqliteOperations(policy);

        var act = () => operations.RunQueryAsync(_db.Secret(), "SELECT 1; VACUUM", 100);

        await act.Should().ThrowAsync<SqliteScriptRefusedException>();
        policy.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_query_over_the_timeout_is_interrupted()
    {
        const string forever = "WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x) SELECT count(*) FROM x";

        var act = () => _db.Operations().RunQueryAsync(_db.Secret(statementTimeoutS: 1), forever, 100);

        await act.Should().ThrowAsync<SqliteStatementTimeoutException>();
    }

    [Fact]
    public async Task Explain_returns_the_plan_of_the_first_statement_only()
    {
        var plan = await _db.Operations().ExplainQueryAsync(_db.Secret(),
            "SELECT * FROM events WHERE name = 'x'; SELECT * FROM numbers");

        plan.Statement.Should().Be("SELECT * FROM events WHERE name = 'x'");
        plan.StatementCount.Should().Be(2);
        plan.Nodes.Should().NotBeEmpty();
        plan.Nodes.Should().Contain(n => n.Detail.Contains("idx_events_name"));
        plan.Nodes.Should().NotContain(n => n.Detail.Contains("numbers"));
    }

    [Fact]
    public async Task Explain_reports_parent_ids_for_nested_plans()
    {
        var plan = await _db.Operations().ExplainQueryAsync(_db.Secret(),
            "SELECT * FROM events WHERE id IN (SELECT n FROM numbers WHERE n > 1)");

        plan.Nodes.Should().Contain(n => n.Parent != 0);
        plan.Nodes.Select(n => n.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Explain_strips_an_explain_prefix_the_user_typed()
    {
        var plan = await _db.Operations().ExplainQueryAsync(_db.Secret(), "EXPLAIN QUERY PLAN SELECT * FROM numbers");

        plan.Statement.Should().Be("SELECT * FROM numbers");
        plan.Nodes.Should().ContainSingle().Which.Detail.Should().StartWith("SCAN");
    }

    [Fact]
    public async Task Explain_does_not_run_a_write()
    {
        await _db.Operations().ExplainQueryAsync(_db.Secret(mode: SqliteAccessMode.ReadWrite), "DELETE FROM events");

        using var check = _db.OpenWriter();
        SqliteSql.Long(check, "SELECT COUNT(*) FROM events").Should().Be(3);
    }

    [Fact]
    public async Task Exports_the_last_result_set_as_csv()
    {
        var export = await _db.Operations().ExportQueryCsvAsync(_db.Secret(),
            "SELECT 1; SELECT id, name, payload FROM events ORDER BY id", 100, CsvExport.MaxBytes);

        export.FileName.Should().Be("query.csv");
        export.RowCount.Should().Be(3);
        export.Truncated.Should().BeFalse();
        Encoding.UTF8.GetString(export.Content).Should().Be("id,name,payload\r\n1,page.view,\r\n2,page.view,AAECAw==\r\n3,export.csv,\r\n");
    }

    [Fact]
    public async Task Query_export_stops_at_the_row_cap()
    {
        var export = await _db.Operations().ExportQueryCsvAsync(_db.Secret(), "SELECT n FROM numbers", 10, CsvExport.MaxBytes);

        export.RowCount.Should().Be(10);
        export.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Query_export_stops_at_the_byte_cap()
    {
        var export = await _db.Operations().ExportQueryCsvAsync(_db.Secret(), "SELECT n FROM numbers", 1000, 20);

        export.Truncated.Should().BeTrue();
        export.Content.Length.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public async Task Query_export_without_a_result_set_is_empty()
    {
        var export = await _db.Operations().ExportQueryCsvAsync(_db.Secret(), "CREATE TEMP TABLE scratch(x)", 10, CsvExport.MaxBytes);

        export.RowCount.Should().Be(0);
        export.Content.Should().BeEmpty();
    }

    public void Dispose() => _db.Dispose();
}
