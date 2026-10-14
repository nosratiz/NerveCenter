using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    /// <summary>
    /// Scalar subquery naming the table a session waits on. A row-lock wait queues on the blocker's transactionid,
    /// so the victim has no ungranted relation lock; its granted 'tuple' lock names the table instead. Ungranted
    /// relation waits sort first. Restricted to the current database (oids are per-database). <paramref name="pidExpr"/>
    /// is a trusted SQL expression, never user text.
    /// </summary>
    private static string WaitingRelationSql(string pidExpr) =>
        "(SELECT l.relation::regclass::text FROM pg_locks l WHERE l.pid = " + pidExpr + " AND l.relation IS NOT NULL " +
        "AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database()) " +
        "AND (NOT l.granted OR l.locktype = 'tuple') ORDER BY l.granted LIMIT 1)";

    private static readonly string BackendSelectSql =
        "SELECT a.pid, coalesce(a.usename,''), coalesce(a.application_name,''), host(a.client_addr), coalesce(a.state,''), " +
        "CASE WHEN a.wait_event_type IS NOT NULL THEN a.wait_event_type || ':' || a.wait_event END, " +
        "coalesce(a.query,''), a.xact_start, a.query_start, now() - coalesce(a.xact_start, a.query_start), " +
        "pg_blocking_pids(a.pid), a.datname, " + WaitingRelationSql("a.pid") +
        " FROM pg_stat_activity a WHERE a.backend_type = 'client backend'";

    public Task<PgActivity> GetActivityAsync(string secret, CancellationToken ct = default) =>
        ReadAsync(secret, null, async (conn, tx) =>
        {
            var backends = new List<PgBackend>();
            await using (var q = await QueryAsync(conn, tx,
                BackendSelectSql + " ORDER BY a.xact_start NULLS LAST, a.pid", ct))
            {
                while (await q.Reader.ReadAsync(ct))
                {
                    backends.Add(MapBackend(q.Reader));
                }
            }

            await using var own = await QueryAsync(conn, tx, "SELECT pg_backend_pid(), now()", ct);
            await own.Reader.ReadAsync(ct);
            return new PgActivity(own.Reader.GetInt32(0), own.Reader.GetFieldValue<DateTimeOffset>(1), backends);
        }, ct);

    public Task<PgBackend?> GetBackendAsync(string secret, int pid, CancellationToken ct = default) =>
        ReadAsync(secret, null, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(BackendSelectSql + " AND a.pid = @pid", conn, tx);
            cmd.Parameters.AddWithValue("pid", pid);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? MapBackend(r) : null;
        }, ct);

    public Task<bool> CancelBackendAsync(string secret, int pid, CancellationToken ct = default) =>
        SignalBackendAsync(secret, pid, "pg_cancel_backend", ct);

    public Task<bool> TerminateBackendAsync(string secret, int pid, CancellationToken ct = default) =>
        SignalBackendAsync(secret, pid, "pg_terminate_backend", ct);

    // Plain autocommit, outside the read-only transaction ReadAsync wraps. The function name is a literal from above.
    private static async Task<bool> SignalBackendAsync(string secret, int pid, string function, CancellationToken ct)
    {
        var settings = PostgresConnectionSettings.Parse(secret);
        if (settings.Mode != PgAccessMode.ReadWrite)
        {
            throw new PgSqlRefusedException(
                "This connection is read-only — set Mode to Read-write in Connections to cancel or terminate backends.");
        }

        await using var conn = Open(settings, null);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {function}(@pid)", conn)
        {
            CommandTimeout = Math.Clamp(settings.StatementTimeoutS, 1, PostgresConnectionSettings.MaxTimeoutS),
        };
        cmd.Parameters.AddWithValue("pid", pid);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static PgBackend MapBackend(NpgsqlDataReader r)
    {
        var state = r.GetString(4);
        return new PgBackend(
            r.GetInt32(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), state,
            r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6),
            r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
            r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            state == "idle" || r.IsDBNull(9) ? null : r.GetTimeSpan(9),
            r.GetFieldValue<int[]>(10), r.IsDBNull(12) ? null : r.GetString(12),
            r.IsDBNull(11) ? null : r.GetString(11));
    }
}
