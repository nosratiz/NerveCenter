using System.Diagnostics;
using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    private const string ExplainPrefix = "EXPLAIN (FORMAT TEXT) ";

    public async Task<PgQueryResult> RunQueryAsync(
        string secret, string? database, PgQueryRequest request, CancellationToken ct = default)
    {
        var settings = PostgresConnectionSettings.Parse(secret);
        var statements = PgSqlGuard.Check(request.Sql, request.Write);
        if (request.Write && settings.Mode != PgAccessMode.ReadWrite)
        {
            throw new PgSqlRefusedException("This connection is read-only — set Mode to Read-write in Connections to write.");
        }

        var timeoutS = Math.Clamp(request.TimeoutS, 1, PostgresConnectionSettings.MaxTimeoutS);
        var cap = Math.Clamp(request.RowCap, 1, 10_000);
        return request.Write
            ? await RunWriteAsync(settings, database, request.Sql, statements, timeoutS, cap, ct)
            : await RunReadAsync(secret, database, request.Sql, statements.Count, FirstTag(statements), timeoutS, cap, ct);
    }

    /// <summary>Test seam: read-mode execution that skips <see cref="PgSqlGuard"/>, to prove the server itself refuses scripts.</summary>
    internal Task<PgQueryResult> RunUnguardedForTestsAsync(
        string secret, string? database, string sql, int timeoutS = 30, int rowCap = 1000, CancellationToken ct = default) =>
        RunReadAsync(secret, database, sql, 1, null, Math.Clamp(timeoutS, 1, PostgresConnectionSettings.MaxTimeoutS), rowCap, ct);

    private static string? FirstTag(IReadOnlyList<PgStatement> statements) => statements.Count == 0 ? null : statements[0].FirstKeyword;

    // Read mode: exactly ONE batch command. The extended protocol makes the server itself refuse a second statement
    // ("cannot insert multiple commands into a prepared statement"), even if the lexer ever disagreed with it.
    private Task<PgQueryResult> RunReadAsync(
        string secret, string? database, string sql, int statementCount, string? tagFallback, int timeoutS, int cap, CancellationToken ct) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            var started = Stopwatch.GetTimestamp();
            await using var batch = new NpgsqlBatch(conn, tx) { Timeout = timeoutS + 5 };
            var command = new NpgsqlBatchCommand(sql);
            batch.BatchCommands.Add(command);
            await using var reader = await batch.ExecuteReaderAsync(ct);
            var (columns, rows, capped) = await ReadLastResultSetAsync(reader, cap, ct);
            await reader.CloseAsync();
            return new PgQueryResult(columns, rows, capped, null, Stopwatch.GetElapsedTime(started), statementCount,
                TagOf(command, tagFallback));
        }, ct, timeoutS);

    private async Task<PgQueryResult> RunWriteAsync(
        PostgresConnectionSettings settings, string? database, string sql, IReadOnlyList<PgStatement> statements,
        int timeoutS, int cap, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await using var conn = Open(settings, database);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var setup = new NpgsqlCommand(
                $"SET LOCAL statement_timeout = '{timeoutS.ToString(System.Globalization.CultureInfo.InvariantCulture)}s'", conn, tx))
            {
                await setup.ExecuteNonQueryAsync(ct);
            }

            // One single-command batch per statement, in order. Npgsql never re-parses the script, and a server error
            // is attributed to its statement exactly (Npgsql 10 pipelines a multi-command batch and surfaces the
            // error at the wrong result set, and leaves PostgresException.BatchCommand null).
            IReadOnlyList<PgResultColumn> columns = [];
            IReadOnlyList<IReadOnlyList<PgCell>> rows = [];
            var capped = false;
            long affected = 0;
            NpgsqlBatchCommand? last = null;
            foreach (var statement in statements)
            {
                var command = new NpgsqlBatchCommand(statement.Text);
                await using var batch = new NpgsqlBatch(conn, tx) { Timeout = timeoutS + 5 };
                batch.BatchCommands.Add(command);
                try
                {
                    await using var reader = await batch.ExecuteReaderAsync(ct);
                    var set = await ReadLastResultSetAsync(reader, cap, ct);
                    await reader.CloseAsync();
                    if (set.Columns.Count > 0)
                    {
                        (columns, rows, capped) = set;
                    }
                }
                catch (PostgresException ex)
                {
                    throw new PgQueryException(ex, statement.Start);
                }

                if (command.StatementType is StatementType.Insert or StatementType.Update or StatementType.Delete or StatementType.Merge)
                {
                    affected += (long)command.Rows;
                }

                last = command;
            }

            await tx.CommitAsync(ct);
            return new PgQueryResult(columns, rows, capped, affected, Stopwatch.GetElapsedTime(started), statements.Count,
                TagOf(last!, statements[^1].FirstKeyword));
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ObjectDisposedException)
            {
                // Broken connection: disposal discards it, and the server aborts the transaction.
            }

            throw;
        }
    }

    private static string? TagOf(NpgsqlBatchCommand command, string? fallback) => command.StatementType switch
    {
        StatementType.Select => "SELECT",
        StatementType.Insert => "INSERT",
        StatementType.Update => "UPDATE",
        StatementType.Delete => "DELETE",
        StatementType.Merge => "MERGE",
        _ => fallback,
    };

    /// <summary>Walks every result set and keeps the last one that has columns (rows capped).</summary>
    private static async Task<(IReadOnlyList<PgResultColumn> Columns, IReadOnlyList<IReadOnlyList<PgCell>> Rows, bool Capped)>
        ReadLastResultSetAsync(NpgsqlDataReader reader, int cap, CancellationToken ct)
    {
        IReadOnlyList<PgResultColumn> columns = [];
        var rows = new List<IReadOnlyList<PgCell>>();
        var capped = false;
        do
        {
            if (reader.FieldCount == 0)
            {
                continue;
            }

            columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new PgResultColumn(reader.GetName(i), reader.GetDataTypeName(i))).ToList();
            rows = [];
            capped = false;
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == cap)
                {
                    capped = true;
                    break;
                }

                var row = new PgCell[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = PgCellFormatter.Format(reader, i);
                }

                rows.Add(row);
            }
        }
        while (await reader.NextResultAsync(ct));

        return (columns, rows, capped);
    }

    public Task<string> ExplainAsync(string secret, string? database, string sql, int timeoutS, CancellationToken ct = default)
    {
        PgSqlGuard.Check(sql, write: false);
        var alreadyExplain = PgSqlGuard.Check(sql, write: false)[0].FirstKeyword == "EXPLAIN";
        var text = alreadyExplain ? sql : ExplainPrefix + sql;
        var prefix = alreadyExplain ? 0 : ExplainPrefix.Length;
        return ReadAsync(secret, database, async (conn, tx) =>
        {
            await using var batch = new NpgsqlBatch(conn, tx) { Timeout = Math.Clamp(timeoutS, 1, PostgresConnectionSettings.MaxTimeoutS) + 5 };
            batch.BatchCommands.Add(new NpgsqlBatchCommand(text));
            try
            {
                await using var reader = await batch.ExecuteReaderAsync(ct);
                var lines = new List<string>();
                while (await reader.ReadAsync(ct))
                {
                    lines.Add(reader.GetString(0));
                }

                return string.Join('\n', lines);
            }
            catch (PostgresException ex)
            {
                throw new PgQueryException(ex, -prefix);
            }
        }, ct, timeoutS);
    }

    public Task<CsvExport> ExportQueryCsvAsync(string secret, string? database, string sql, int timeoutS, CancellationToken ct = default)
    {
        PgSqlGuard.Check(sql, write: false);
        return ReadAsync(secret, database, async (conn, tx) =>
        {
            await using var batch = new NpgsqlBatch(conn, tx) { Timeout = Math.Clamp(timeoutS, 1, PostgresConnectionSettings.MaxTimeoutS) + 5 };
            batch.BatchCommands.Add(new NpgsqlBatchCommand(sql));
            await using var reader = await batch.ExecuteReaderAsync(ct);
            var (content, count, truncated) = await WriteCsvAsync(reader, CsvExport.EffectiveMaxRows, CsvExport.MaxBytes, ct);
            return new CsvExport($"query-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv", content, count, truncated);
        }, ct, timeoutS);
    }
}
