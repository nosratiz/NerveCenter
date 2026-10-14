using FluentAssertions;
using Npgsql;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Infrastructure;

namespace SbConsole.Plugins.Postgres.Tests.Client;

[Collection("postgres")]
public sealed class OperationsQueryTests(PostgresFixture fx)
{
    private static PgQueryRequest Read(string sql, int cap = 1000, int timeout = 30) => new(sql, false, cap, timeout);

    private static PgQueryRequest Write(string sql) => new(sql, true, 1000, 30);

    private string Rw => fx.Secret(PgAccessMode.ReadWrite);

    private async Task<string> ScratchAsync(string rows = "(1),(2),(3)")
    {
        var name = "scratch_" + Guid.NewGuid().ToString("N")[..10];
        await AdminAsync($"CREATE TABLE {name} (id int); INSERT INTO {name} VALUES {rows}");
        return name;
    }

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.AdminConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table)
    {
        await using var conn = new NpgsqlConnection(fx.AdminConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [DockerFact]
    public async Task Select_returns_typed_columns_and_cells()
    {
        var r = await fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT 'a'::text AS t, 5::bigint AS b, 1.5::numeric AS n"));

        r.Columns.Select(c => (c.Name, c.TypeName)).Should().Equal(("t", "text"), ("b", "bigint"), ("n", "numeric"));
        r.Rows.Should().ContainSingle();
        r.Rows[0].Select(c => c.Display).Should().Equal("a", "5", "1.5");
        r.Capped.Should().BeFalse();
        r.StatementCount.Should().Be(1);
        r.CommandTag.Should().Be("SELECT");
        r.RowsAffected.Should().BeNull();
    }

    [DockerFact]
    public async Task Row_cap_marks_the_result_capped()
    {
        var r = await fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT g FROM generate_series(1, 50) g", cap: 10));

        r.Rows.Should().HaveCount(10);
        r.Capped.Should().BeTrue();
    }

    [DockerFact]
    public async Task Read_mode_runs_a_trailing_semicolon_and_a_leading_comment()
    {
        var r = await fx.Operations.RunQueryAsync(fx.Secret(), null, Read("-- hi\nSELECT 7;"));
        r.Rows[0][0].Display.Should().Be("7");
    }

    [DockerFact]
    public async Task Read_mode_refuses_an_update_with_25006()
    {
        var ex = await FluentActions.Awaiting(() =>
            fx.Operations.RunQueryAsync(fx.Secret(), null, Read("UPDATE customers SET country = 'XX'"))).Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("25006");
    }

    [DockerFact]
    public async Task Read_mode_refuses_multiple_statements_before_the_server()
    {
        await FluentActions.Awaiting(() => fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT 1; SELECT 2")))
            .Should().ThrowAsync<PgSqlRefusedException>();
        await FluentActions.Awaiting(() => fx.Operations.RunQueryAsync(fx.Secret(), null, Read("COMMIT; DELETE FROM customers")))
            .Should().ThrowAsync<PgSqlRefusedException>();
    }

    [DockerFact]
    public async Task The_server_itself_refuses_a_script_in_read_mode_even_without_the_guard()
    {
        var ex = await FluentActions.Awaiting(() =>
            fx.Operations.RunUnguardedForTestsAsync(fx.Secret(), null, "SELECT 1; SELECT 2")).Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("42601");
    }

    [DockerFact]
    public async Task Commit_then_delete_in_read_mode_deletes_nothing_even_without_the_guard()
    {
        var table = await ScratchAsync();

        await FluentActions.Awaiting(() =>
            fx.Operations.RunUnguardedForTestsAsync(fx.Secret(), null, $"SELECT 1; COMMIT; DELETE FROM {table}")).Should().ThrowAsync<PostgresException>();

        (await CountAsync(table)).Should().Be(3);
    }

    [DockerFact]
    public async Task A_one_second_timeout_cancels_pg_sleep_with_57014()
    {
        var ex = await FluentActions.Awaiting(() =>
            fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT pg_sleep(3)", timeout: 1))).Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("57014");
    }

    [DockerFact]
    public async Task Write_on_a_read_only_connection_is_refused()
    {
        var ex = await FluentActions.Awaiting(() =>
            fx.Operations.RunQueryAsync(fx.Secret(), null, Write("SELECT 1"))).Should().ThrowAsync<PgSqlRefusedException>();
        ex.Which.Message.Should().Contain("read-only");
    }

    [DockerFact]
    public async Task Write_commits_and_reports_rows_affected()
    {
        var table = await ScratchAsync();

        var r = await fx.Operations.RunQueryAsync(Rw, null, Write($"INSERT INTO {table} VALUES (10),(11); UPDATE {table} SET id = id + 100 WHERE id > 2"));

        r.RowsAffected.Should().Be(5);
        r.StatementCount.Should().Be(2);
        r.CommandTag.Should().Be("UPDATE");
        (await CountAsync(table)).Should().Be(5);
    }

    [DockerFact]
    public async Task Write_returns_the_last_result_set_with_columns()
    {
        var table = await ScratchAsync();

        var r = await fx.Operations.RunQueryAsync(Rw, null, Write($"INSERT INTO {table} VALUES (9) RETURNING id; SELECT count(*) AS n FROM {table}; DELETE FROM {table} WHERE id = 9"));

        r.Columns.Select(c => c.Name).Should().Equal("n");
        r.Rows[0][0].Display.Should().Be("4");
        r.RowsAffected.Should().Be(2);
        r.CommandTag.Should().Be("DELETE");
    }

    [DockerFact]
    public async Task A_failing_script_rolls_back_every_statement()
    {
        var table = await ScratchAsync();

        await FluentActions.Awaiting(() => fx.Operations.RunQueryAsync(Rw, null,
            Write($"INSERT INTO {table} VALUES (99); SELECT nope FROM {table}"))).Should().ThrowAsync<PgQueryException>();

        (await CountAsync(table)).Should().Be(3);
    }

    [DockerFact]
    public async Task An_undefined_column_on_line_three_of_a_script_reports_line_three()
    {
        var table = await ScratchAsync();
        var script = $"INSERT INTO {table} VALUES (1);\nSELECT 1;\nSELECT   nope FROM {table}";

        var ex = await FluentActions.Awaiting(() => fx.Operations.RunQueryAsync(Rw, null, Write(script))).Should().ThrowAsync<PgQueryException>();
        var info = FriendlyPostgresError.Describe(ex.Which, script);

        info.Line.Should().Be(3);
        info.Column.Should().Be(10);
        info.SqlState.Should().Be("42703");
    }

    [DockerFact]
    public async Task An_undefined_column_in_a_single_read_statement_maps_to_its_line()
    {
        const string sql = "SELECT 1\nFROM customers\nWHERE nope = 1";
        var ex = await FluentActions.Awaiting(() => fx.Operations.RunQueryAsync(fx.Secret(), null, Read(sql))).Should().ThrowAsync<PostgresException>();

        FriendlyPostgresError.Describe(ex.Which, sql).Line.Should().Be(3);
    }

    [DockerFact]
    public async Task Explain_returns_the_plan_text()
    {
        var plan = await fx.Operations.ExplainAsync(fx.Secret(), null, "SELECT * FROM customers", 30);
        plan.Should().Contain("Seq Scan");
    }

    [DockerFact]
    public async Task Explain_runs_an_explicit_explain_as_is()
    {
        var plan = await fx.Operations.ExplainAsync(fx.Secret(), null, "EXPLAIN (COSTS OFF) SELECT * FROM customers", 30);
        plan.Should().StartWith("Seq Scan");
    }

    [DockerFact]
    public async Task Explain_error_positions_ignore_the_added_prefix()
    {
        const string sql = "SELECT nope FROM customers";
        var ex = await FluentActions.Awaiting(() => fx.Operations.ExplainAsync(fx.Secret(), null, sql, 30)).Should().ThrowAsync<PgQueryException>();
        var info = FriendlyPostgresError.Describe(ex.Which, sql);
        info.Line.Should().Be(1);
        info.Column.Should().Be(8);
    }

    [DockerFact]
    public async Task Explain_refuses_a_script()
    {
        await FluentActions.Awaiting(() => fx.Operations.ExplainAsync(fx.Secret(), null, "SELECT 1; SELECT 2", 30))
            .Should().ThrowAsync<PgSqlRefusedException>();
    }

    [DockerFact]
    public async Task Export_writes_header_and_rows()
    {
        var csv = await fx.Operations.ExportQueryCsvAsync(fx.Secret(), null, "SELECT id, country FROM customers ORDER BY id LIMIT 3", 30);

        csv.FileName.Should().MatchRegex(@"^query-\d{8}-\d{6}\.csv$");
        csv.RowCount.Should().Be(3);
        csv.Truncated.Should().BeFalse();
        System.Text.Encoding.UTF8.GetString(csv.Content).Should().StartWith("id,country\r\n1,");
    }

    [DockerFact]
    public async Task Export_refuses_a_script()
    {
        await FluentActions.Awaiting(() => fx.Operations.ExportQueryCsvAsync(fx.Secret(), null, "SELECT 1; SELECT 2", 30))
            .Should().ThrowAsync<PgSqlRefusedException>();
    }

    [DockerFact]
    public async Task Export_truncates_at_the_row_cap()
    {
        CsvExport.MaxRowsOverride = 5;
        try
        {
            var csv = await fx.Operations.ExportQueryCsvAsync(fx.Secret(), null, "SELECT g FROM generate_series(1, 20) g", 30);
            csv.RowCount.Should().Be(5);
            csv.Truncated.Should().BeTrue();
        }
        finally
        {
            CsvExport.MaxRowsOverride = null;
        }
    }

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);

    [DockerFact]
    public async Task Read_mode_cap_on_a_huge_select_cancels_instead_of_draining()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await fx.Operations.RunQueryAsync(fx.Secret(), null,
            Read("SELECT g FROM generate_series(1, 50000000) g", cap: 10, timeout: 5));
        sw.Stop();

        r.Capped.Should().BeTrue();
        r.Rows.Should().HaveCount(10);
        sw.Elapsed.Should().BeLessThan(Quick);
        (await fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT 1"))).Rows.Should().ContainSingle();
    }

    [DockerFact]
    public async Task Query_export_cap_on_a_huge_select_cancels_instead_of_draining()
    {
        CsvExport.MaxRowsOverride = 10;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var csv = await fx.Operations.ExportQueryCsvAsync(fx.Secret(), null, "SELECT g FROM generate_series(1, 50000000) g", 5);
            sw.Stop();

            csv.Truncated.Should().BeTrue();
            csv.RowCount.Should().Be(10);
            sw.Elapsed.Should().BeLessThan(Quick);
        }
        finally
        {
            CsvExport.MaxRowsOverride = null;
        }

        (await fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT 1"))).Rows.Should().ContainSingle();
    }

    [DockerFact]
    public async Task Explain_analyze_of_a_delete_is_refused_and_changes_nothing()
    {
        var table = await ScratchAsync();

        await FluentActions.Awaiting(() =>
            fx.Operations.ExplainAsync(fx.Secret(PgAccessMode.ReadWrite), null, $"EXPLAIN ANALYZE DELETE FROM {table}", 30)).Should().ThrowAsync<PgSqlRefusedException>();

        (await CountAsync(table)).Should().Be(3);
    }

    [DockerFact]
    public async Task Explain_tab_refuses_the_parenthesised_analyze_option()
    {
        var table = await ScratchAsync();

        await FluentActions.Awaiting(() => fx.Operations.ExplainAsync(fx.Secret(), null, $"explain (costs off, analyze) SELECT * FROM {table}", 30))
            .Should().ThrowAsync<PgSqlRefusedException>().WithMessage("EXPLAIN ANALYZE runs the statement — use Run, which is audited.");
    }

    [DockerFact]
    public async Task Run_still_runs_explain_analyze()
    {
        var table = await ScratchAsync();

        var r = await fx.Operations.RunQueryAsync(fx.Secret(), null, Read($"EXPLAIN ANALYZE SELECT * FROM {table}"));

        r.Rows.Should().NotBeEmpty();
    }

    [DockerFact]
    public async Task Read_mode_refuses_reindex()
    {
        var table = await ScratchAsync();

        await FluentActions.Awaiting(() => fx.Operations.RunQueryAsync(fx.Secret(), null, Read($"REINDEX TABLE {table}")))
            .Should().ThrowAsync<PgSqlRefusedException>();
    }

    [DockerFact]
    public async Task Read_mode_explain_analyze_of_a_delete_is_refused_and_changes_nothing()
    {
        var table = await ScratchAsync();

        var ex = await FluentActions.Awaiting(() =>
            fx.Operations.RunQueryAsync(fx.Secret(PgAccessMode.ReadWrite), null, Read($"EXPLAIN ANALYZE DELETE FROM {table}"))).Should().ThrowAsync<Exception>();

        SqlStateOf(ex.Which).Should().Be("25006");
        (await CountAsync(table)).Should().Be(3);
    }

    private static string? SqlStateOf(Exception ex) =>
        ex as PostgresException is { } pg ? pg.SqlState : (ex as PgQueryException)?.Inner.SqlState;

    [DockerFact]
    public async Task Timeout_zero_is_treated_as_one_second()
    {
        var r = await fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT 1", timeout: 0));
        r.Rows.Should().ContainSingle();
    }

    [DockerFact]
    public async Task Row_cap_zero_is_treated_as_one_row()
    {
        var r = await fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT g FROM generate_series(1, 5) g", cap: 0));
        r.Rows.Should().ContainSingle();
        r.Capped.Should().BeTrue();
    }

    [DockerFact]
    public async Task Row_cap_above_ten_thousand_is_clamped()
    {
        var r = await fx.Operations.RunQueryAsync(fx.Secret(), null, Read("SELECT g FROM generate_series(1, 20000) g", cap: 50_000));
        r.Rows.Should().HaveCount(10_000);
        r.Capped.Should().BeTrue();
    }

    [DockerFact]
    public async Task Unguarded_seam_clamps_the_row_cap()
    {
        var r = await fx.Operations.RunUnguardedForTestsAsync(fx.Secret(), null, "SELECT g FROM generate_series(1, 20000) g", rowCap: 50_000);
        r.Rows.Should().HaveCount(10_000);
        var one = await fx.Operations.RunUnguardedForTestsAsync(fx.Secret(), null, "SELECT g FROM generate_series(1, 5) g", rowCap: 0);
        one.Rows.Should().ContainSingle();
    }

    [DockerFact]
    public async Task Write_mode_refuses_a_script_containing_commit()
    {
        var table = await ScratchAsync();
        await FluentActions.Awaiting(() =>
            fx.Operations.RunQueryAsync(fx.Secret(PgAccessMode.ReadWrite), null, Write($"DELETE FROM {table}; COMMIT")))
            .Should().ThrowAsync<PgSqlRefusedException>();
        (await CountAsync(table)).Should().Be(3);
    }
}
