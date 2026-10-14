using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Write mode (mockup 1d, design spec §11.4): user SQL on writable, attach-limited connections, one transaction per script.</summary>
public sealed partial class SqliteOperations
{
    public Task<WriteScriptResult> DryRunWriteAsync(string secret, string sql, CancellationToken ct = default) =>
        RunScriptAsync(secret, sql, SqliteOpenOptions.UserWrite, (c, script) => ExecuteWrite(c, script, commit: false), ct);

    public Task<WriteScriptResult> RunWriteAsync(string secret, string sql, CancellationToken ct = default) =>
        RunScriptAsync(secret, sql, SqliteOpenOptions.UserWrite, (c, script) => ExecuteWrite(c, script, commit: true), ct);

    public Task<WalCheckpointResult> CheckpointWalAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, SqliteOpenOptions.PluginWrite, c => SqliteSql.Rows(c, "PRAGMA wal_checkpoint(TRUNCATE)",
            r => new WalCheckpointResult(r.GetInt64(0) != 0, r.GetInt64(1), r.GetInt64(2)))[0], ct);

    /// <summary>
    /// BEGIN IMMEDIATE, every statement with its changes() and total_changes() delta, then COMMIT
    /// (or, for a dry run, always ROLLBACK). Any error rolls back and nothing is kept.
    /// </summary>
    private WriteScriptResult ExecuteWrite(SqliteConnection c, SqliteScript script, bool commit)
    {
        var started = Stopwatch.GetTimestamp();
        var results = new List<WriteStatementResult>();
        // Plain SQL rather than SqliteConnection.BeginTransaction: the helpers' commands carry no
        // SqliteTransaction, which Microsoft.Data.Sqlite would refuse inside one.
        SqliteSql.Execute(c, "BEGIN IMMEDIATE");
        try
        {
            foreach (var statement in script.Statements)
            {
                var direct = ChangesRows(c, statement);
                var before = raw.sqlite3_total_changes(c.Handle);
                SqliteSql.Execute(c, statement.Text);
                var directRows = direct ? raw.sqlite3_changes(c.Handle) : 0;
                var allRows = raw.sqlite3_total_changes(c.Handle) - before;
                results.Add(new WriteStatementResult(statement.Keyword, statement.Text, directRows, Math.Max(0, allRows - directRows)));
            }

            if (commit)
            {
                SqliteSql.Execute(c, "COMMIT");
            }
            else
            {
                RollbackQuietly(c);
            }
        }
        catch
        {
            RollbackQuietly(c);
            throw;
        }

        var duration = Stopwatch.GetElapsedTime(started);
        return new WriteScriptResult(commit, results, duration, commit ? FileStateAfterCommit(c) : null);
    }

    /// <summary>
    /// changes() only moves on INSERT/UPDATE/DELETE (it keeps the previous value across DDL and
    /// SELECT), so anything else reports 0 direct rows. A WITH statement is one of them only when it
    /// isn't read-only; EXPLAIN never runs the statement it explains.
    /// </summary>
    private static bool ChangesRows(SqliteConnection c, SqliteStatement statement) => !statement.IsExplain && statement.Keyword switch
    {
        "INSERT" or "UPDATE" or "DELETE" or "REPLACE" => true,
        "WITH" => !IsReadOnlyStatement(c, statement.Text),
        _ => false,
    };

    private static bool IsReadOnlyStatement(SqliteConnection c, string sql)
    {
        var rc = raw.sqlite3_prepare_v2(c.Handle, sql, out var stmt);
        using (stmt)
        {
            // A statement that doesn't prepare fails on execution right after; the answer doesn't matter.
            return rc != raw.SQLITE_OK || stmt.IsInvalid || raw.sqlite3_stmt_readonly(stmt) != 0;
        }
    }

    /// <summary>
    /// An interrupted INSERT/UPDATE/DELETE may already have rolled the transaction back (autocommit
    /// is on again), and a failed ROLLBACK is only logged: closing the connection, which Run always
    /// does next, rolls back whatever is still open.
    /// </summary>
    private void RollbackQuietly(SqliteConnection c)
    {
        if (raw.sqlite3_get_autocommit(c.Handle) != 0)
        {
            return;
        }

        try
        {
            SqliteSql.Execute(c, "ROLLBACK");
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "ROLLBACK failed on {Path}; closing the connection rolls back instead.", c.DataSource);
        }
    }

    /// <summary>The committed write already happened: a failure (or a late timeout) reading the file state only drops the panel.</summary>
    private WriteFileState? FileStateAfterCommit(SqliteConnection c)
    {
        try
        {
            var wal = new FileInfo(c.DataSource + "-wal");
            var pages = SqliteSql.Long(c, "PRAGMA page_count");
            var free = SqliteSql.Long(c, "PRAGMA freelist_count");
            return new WriteFileState(wal.Exists ? wal.Length : null, pages == 0 ? 0 : 100.0 * free / pages);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            _logger.LogWarning(ex, "Reading the file state after a committed write on {Path} failed.", c.DataSource);
            return null;
        }
    }
}
