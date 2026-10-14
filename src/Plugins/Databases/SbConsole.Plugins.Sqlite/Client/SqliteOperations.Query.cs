using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Query page (mockup 1c, design spec §11.3): user SQL on read-only, attach-limited connections -- the live file or a backup (§11.7).</summary>
public sealed partial class SqliteOperations
{
    public Task<QueryResult> RunQueryAsync(string secret, string sql, int limit, string? backupFile = null, CancellationToken ct = default) =>
        RunScriptAsync(secret, sql, QueryOptions(backupFile), (c, script) =>
        {
            var started = Stopwatch.GetTimestamp();
            (int Index, IReadOnlyList<QueryColumn> Columns, List<IReadOnlyList<CellValue>> Rows, long Total, bool Capped)? last = null;
            RunStatements(c, script, (index, statement, reader) =>
            {
                var (rows, total, capped) = ReadResultRows(reader, Math.Max(0, limit));
                last = (index, ResultColumns(c, statement, reader, rows), rows, total, capped);
            });

            var duration = Stopwatch.GetElapsedTime(started);
            return last is { } result
                ? new QueryResult(result.Columns, result.Rows, result.Total, result.Capped, script.Statements.Count, result.Index + 1, duration)
                : new QueryResult([], [], 0, false, script.Statements.Count, null, duration);
        }, ct);

    /// <summary>EXPLAIN QUERY PLAN of the first statement only -- the rest of the script never runs.</summary>
    public Task<ExplainPlan> ExplainQueryAsync(string secret, string sql, string? backupFile = null, CancellationToken ct = default) =>
        RunScriptAsync(secret, sql, QueryOptions(backupFile), (c, script) =>
        {
            var first = script.Statements[0];
            var nodes = SqliteSql.Rows(c, "EXPLAIN QUERY PLAN " + first.Body, r => new ExplainNode(r.GetInt32(0), r.GetInt32(1), r.GetString(3)));
            return new ExplainPlan(first.Body, script.Statements.Count, nodes);
        }, ct);

    /// <summary>The Query page's Export CSV: the same result set the grid shows (the last one), with the table export's caps.</summary>
    public Task<CsvExport> ExportQueryCsvAsync(string secret, string sql, int maxRows, long maxBytes, string? backupFile = null, CancellationToken ct = default) =>
        RunScriptAsync(secret, sql, QueryOptions(backupFile), (c, script) =>
        {
            // Which result set is last is only known once the script has run (a statement can depend
            // on an earlier one's temp table), so every result set is written and the last one kept.
            (byte[] Content, int RowCount, bool Truncated) csv = ([], 0, false);
            RunStatements(c, script, (_, _, reader) => csv = WriteCsv(reader, maxRows, maxBytes));
            return new CsvExport("query.csv", csv.Content, csv.RowCount, csv.Truncated);
        }, ct);

    /// <summary>
    /// The live file, or with <paramref name="backupFile"/> that file inside the connection's
    /// BackupDir (§11.7 "Open read-only") -- the same read-only, attach-limited open either way.
    /// </summary>
    private static SqliteOpenOptions QueryOptions(string? backupFile) => SqliteOpenOptions.UserQuery with { BackupFile = backupFile };

    /// <summary>
    /// Parses (and so refuses, §11.2) the script before anything is resolved or opened, then runs
    /// <paramref name="work"/> like RunAsync.
    /// </summary>
    private Task<T> RunScriptAsync<T>(string secret, string sql, SqliteOpenOptions options, Func<SqliteConnection, SqliteScript, T> work, CancellationToken ct) =>
        Task.Run(() =>
        {
            var script = SqliteScript.Parse(sql);
            return Run(secret, options, c => work(c, script), ct);
        }, ct);

    /// <summary>Runs every statement in order and hands each one that returns columns to <paramref name="onResultSet"/> (0-based index).</summary>
    private static void RunStatements(SqliteConnection c, SqliteScript script, Action<int, SqliteStatement, SqliteDataReader> onResultSet)
    {
        for (var i = 0; i < script.Statements.Count; i++)
        {
            using var command = c.CreateCommand();
            command.CommandText = script.Statements[i].Text;
            using var reader = command.ExecuteReader();
            if (reader.FieldCount > 0)
            {
                onResultSet(i, script.Statements[i], reader);
            }
        }
    }

    /// <summary>Materialises up to <paramref name="limit"/> rows and keeps counting the rest up to QueryResult.CountCap.</summary>
    private static (List<IReadOnlyList<CellValue>> Rows, long Total, bool Capped) ReadResultRows(SqliteDataReader reader, int limit)
    {
        var rows = new List<IReadOnlyList<CellValue>>();
        long total = 0;
        while (reader.Read())
        {
            if (total == QueryResult.CountCap)
            {
                return (rows, total, true);
            }

            total++;
            if (rows.Count < limit)
            {
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => ReadCell(reader, i)).ToList());
            }
        }

        return (rows, total, false);
    }

    private static List<QueryColumn> ResultColumns(SqliteConnection c, SqliteStatement statement, SqliteDataReader reader, List<IReadOnlyList<CellValue>> rows)
    {
        var declared = DeclaredTypes(c, statement.Text);
        return Enumerable.Range(0, reader.FieldCount).Select(i =>
        {
            var cells = rows.Select(r => r[i]).ToList();
            var integers = cells.Where(cell => cell.Kind == CellKind.Integer).ToList();
            var unixMs = ColumnSample.LooksLikeUnixMsValues(
                cells.Count(cell => cell.Kind != CellKind.Null),
                integers.Count,
                integers.Count(cell => cell.Integer is { } value && ColumnSample.InEpochMsRange(value)));
            return new QueryColumn(reader.GetName(i), i < declared.Count ? declared[i] : null, unixMs);
        }).ToList();
    }

    /// <summary>
    /// sqlite3_column_decltype per column (null for expressions). Microsoft.Data.Sqlite's
    /// GetDataTypeName falls back to the current row's storage class, which would make an
    /// expression look declared, so the statement is prepared once more through the raw API.
    /// </summary>
    private static List<string?> DeclaredTypes(SqliteConnection c, string sql)
    {
        var rc = raw.sqlite3_prepare_v2(c.Handle, sql, out var stmt);
        using (stmt)
        {
            if (rc != raw.SQLITE_OK || stmt.IsInvalid)
            {
                return [];
            }

            return Enumerable.Range(0, raw.sqlite3_column_count(stmt))
                .Select(i => (string?)raw.sqlite3_column_decltype(stmt, i).utf8_to_string())
                .ToList();
        }
    }
}
