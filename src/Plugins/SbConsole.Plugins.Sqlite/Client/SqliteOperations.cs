using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SbConsole.Sdk;
using SQLitePCL;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// Microsoft.Data.Sqlite implementation. Every call: parse the secret, ask IHostPathPolicy, open the
/// *resolved* path read-only with pooling off, set busy_timeout, run the work under the statement
/// timeout, close. Design spec §4.2.
/// </summary>
public sealed partial class SqliteOperations(IHostPathPolicy pathPolicy, ILogger<SqliteOperations>? logger = null) : ISqliteOperations, ISingletonService
{
    private readonly ILogger<SqliteOperations> _logger = logger ?? NullLogger<SqliteOperations>.Instance;

    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        SqliteConnectionSettings settings;
        try
        {
            settings = SqliteConnectionSettings.Parse(secret);
        }
        catch (FormatException ex)
        {
            var message = FriendlySqliteError.From(ex);
            return new ConnectionTestResult(false, message, Checks: [new ConnectionCheck("Settings valid", ConnectionCheckStatus.Failed, message)]);
        }

        var path = pathPolicy.Check(settings.Path);
        if (!path.Allowed)
        {
            var reason = path.Reason ?? "Path refused.";
            return new ConnectionTestResult(false, reason, Checks: [new ConnectionCheck("Path allowed", ConnectionCheckStatus.Failed, reason)]);
        }

        List<ConnectionCheck> checks = [new ConnectionCheck("Path allowed", ConnectionCheckStatus.Passed, path.ResolvedPath)];
        try
        {
            var (version, journal, tables, bytes) = await RunAsync(secret, c => (
                SqliteSql.Text(c, "SELECT sqlite_version()"),
                SqliteSql.Text(c, "PRAGMA journal_mode").ToUpperInvariant(),
                SqliteSql.Long(c, @"SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\'"),
                new FileInfo(c.DataSource).Length), ct);

            var identity = $"SQLite {version} · {SqliteFormat.Bytes(bytes)} · {journal}";
            checks.Add(new ConnectionCheck("File opened", ConnectionCheckStatus.Passed, identity));
            checks.Add(new ConnectionCheck("Schema readable", ConnectionCheckStatus.Passed, $"{tables.ToString(CultureInfo.InvariantCulture)} tables"));
            return new ConnectionTestResult(true, null, identity, checks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SQLite connection test failed for {Path}", path.ResolvedPath);
            var message = FriendlySqliteError.From(ex);
            checks.Add(new ConnectionCheck("File opened", ConnectionCheckStatus.Failed, message));
            return new ConnectionTestResult(false, message, null, checks);
        }
    }

    /// <summary>Runs <paramref name="work"/> off the Blazor circuit thread (Microsoft.Data.Sqlite's async API is synchronous underneath).</summary>
    internal Task<T> RunAsync<T>(string secret, Func<SqliteConnection, T> work, CancellationToken ct) =>
        Task.Run(() => Run(secret, work, ct), ct);

    internal T Run<T>(string secret, Func<SqliteConnection, T> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = SqliteConnectionSettings.Parse(secret);
        var path = pathPolicy.Check(settings.Path);
        if (!path.Allowed)
        {
            throw new SqlitePathRefusedException(path.Reason ?? "Path refused.");
        }

        using var connection = Open(path.ResolvedPath!, settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.StatementTimeoutS));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        // Verified 2026-10-13 (Microsoft.Data.Sqlite 10.0.12): a token passed to the provider, or
        // SqliteCommand.Cancel(), does NOT stop a running statement. sqlite3_interrupt does, with
        // SQLITE_INTERRUPT. But an interrupt that lands while no statement is running is cleared when
        // the next statement starts, and a linked token fires only once, so the interrupt alone can be
        // lost (cancel during Open/PRAGMA, or a timeout between two statements of `work`). Two
        // backstops: a progress handler that aborts every statement while the token is cancelled
        // (also SQLITE_INTERRUPT), and a token check before `work` starts.
        // Disposing the registration waits for an in-flight callback, so the handle is never
        // interrupted after the connection is gone. The progress handler is not cleared explicitly: it
        // only runs while a statement executes on this connection, which can't happen after `Run`
        // returns, and it is released when the connection is disposed. IsCancelled tolerates `linked`
        // being disposed first (using-declarations unwind in reverse order).
        raw.sqlite3_progress_handler(connection.Handle, 1000, _ => IsCancelled(linked) ? 1 : 0, null);
        using (linked.Token.Register(() => raw.sqlite3_interrupt(connection.Handle)))
        {
            try
            {
                if (linked.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new SqliteStatementTimeoutException(settings.StatementTimeoutS, null);
                }

                return work(connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
            {
                ct.ThrowIfCancellationRequested();
                throw new SqliteStatementTimeoutException(settings.StatementTimeoutS, ex);
            }
        }
    }

    private static bool IsCancelled(CancellationTokenSource source)
    {
        try
        {
            return source.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static SqliteConnection Open(string resolvedPath, SqliteConnectionSettings settings)
    {
        // The resolved path is always absolute ("/..."), so SQLite can never read it as a "file:"
        // URI carrying its own ?mode= override.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = resolvedPath,
            Mode = SqliteOpenMode.ReadOnly, // phase 1: always read-only, whatever settings.Mode says (spec §2)
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(settings.BusyTimeoutMs / 1000.0)),
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            SqliteSql.Execute(connection, $"PRAGMA busy_timeout = {settings.BusyTimeoutMs.ToString(CultureInfo.InvariantCulture)}");
            // Defence in depth for untrusted files: a hostile schema can't run SQL functions from views/triggers/defaults.
            SqliteSql.Execute(connection, "PRAGMA trusted_schema = OFF");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
