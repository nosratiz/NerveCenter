using System.Globalization;
using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    private static readonly TimeSpan IdleInTransactionAttention = TimeSpan.FromMinutes(5);

    public Task<PgOverview> GetOverviewAsync(string secret, CancellationToken ct = default) =>
        ReadAsync(secret, null, (conn, tx) => ReadOverviewAsync(conn, tx, ct), ct);

    public Task<int> GetBlockedCountAsync(string secret, CancellationToken ct = default) =>
        ReadAsync(secret, null, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE cardinality(pg_blocking_pids(pid)) > 0", conn, tx);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }, ct);

    private static async Task<PgOverview> ReadOverviewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        string version;
        bool replica;
        DateTimeOffset started, captured;
        int maxConnections;
        string sharedBuffers;
        long xidAge;
        await using (var q = await QueryAsync(conn, tx, """
            SELECT current_setting('server_version'), pg_is_in_recovery(), pg_postmaster_start_time(), now(),
                   current_setting('max_connections')::int, current_setting('shared_buffers'),
                   (SELECT max(age(datfrozenxid)) FROM pg_database)::bigint
            """, ct))
        {
            var r = q.Reader;
            await r.ReadAsync(ct);
            version = r.GetString(0);
            replica = r.GetBoolean(1);
            started = r.GetFieldValue<DateTimeOffset>(2);
            captured = r.GetFieldValue<DateTimeOffset>(3);
            maxConnections = r.GetInt32(4);
            sharedBuffers = r.GetString(5);
            xidAge = r.IsDBNull(6) ? 0 : r.GetInt64(6);
        }

        // pg_stat_activity blanks backend_type, state, wait_event and the timestamps of other users' sessions unless the
        // role is a superuser or has pg_read_all_stats, so a plain count(backend_type = 'client backend') sees only
        // the role's own sessions. "Hidden" counts client-or-unknown rows of other users whose state is NULL.
        int clientBackends, idle, idleInTxn, waiting, hidden;
        bool privileged;
        long? numBackends;
        await using (var q = await QueryAsync(conn, tx, """
            SELECT count(*) FILTER (WHERE backend_type = 'client backend'),
                   count(*) FILTER (WHERE state = 'idle'),
                   count(*) FILTER (WHERE state LIKE 'idle in transaction%'),
                   count(*) FILTER (WHERE wait_event_type = 'Lock'),
                   count(*) FILTER (WHERE state IS NULL AND usename IS NOT NULL AND usename <> current_user
                                      AND (backend_type IS NULL OR backend_type = 'client backend')),
                   (SELECT rolsuper OR pg_has_role(oid, 'pg_read_all_stats', 'member') FROM pg_roles WHERE rolname = current_user),
                   (SELECT sum(numbackends) FROM pg_stat_database)
              FROM pg_stat_activity WHERE pid <> pg_backend_pid()
            """, ct))
        {
            var r = q.Reader;
            await r.ReadAsync(ct);
            clientBackends = Int(r, 0);
            idle = Int(r, 1);
            idleInTxn = Int(r, 2);
            waiting = Int(r, 3);
            hidden = Int(r, 4);
            privileged = !r.IsDBNull(5) && r.GetBoolean(5);
            numBackends = r.IsDBNull(6) ? null : Convert.ToInt64(r.GetValue(6), CultureInfo.InvariantCulture);
        }

        // When other users' sessions are hidden the activity view undercounts; pg_stat_database.numbackends is not
        // permission-gated (it includes this connection, which the +1 below adds back).
        if (hidden > 0 && numBackends is { } total)
        {
            clientBackends = Math.Max(clientBackends, (int)Math.Min(int.MaxValue, total - 1));
        }

        PgLongestTransaction? longest = null;
        await using (var q = await QueryAsync(conn, tx, """
            SELECT pid, coalesce(usename,''), coalesce(application_name,''), coalesce(state,''), now() - xact_start
              FROM pg_stat_activity
             WHERE xact_start IS NOT NULL AND pid <> pg_backend_pid() AND backend_type = 'client backend'
             ORDER BY xact_start LIMIT 1
            """, ct))
        {
            var r = q.Reader;
            if (await r.ReadAsync(ct))
            {
                longest = new PgLongestTransaction(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetTimeSpan(4));
            }
        }

        var databases = new List<PgDatabaseStat>();
        long hitSum = 0, readSum = 0, knownBytes = 0;
        await using (var q = await QueryAsync(conn, tx, """
            SELECT d.datname, CASE WHEN has_database_privilege(d.oid, 'CONNECT') OR pg_has_role('pg_read_all_stats', 'member') THEN pg_database_size(d.oid) END, pg_get_userbyid(d.datdba), coalesce(s.numbackends,0),
                   CASE WHEN s.blks_hit + s.blks_read > 0 THEN s.blks_hit::float8 / (s.blks_hit + s.blks_read) END,
                   coalesce(s.xact_commit,0), coalesce(s.deadlocks,0), s.blks_hit, s.blks_read
              FROM pg_database d LEFT JOIN pg_stat_database s ON s.datid = d.oid
             WHERE d.datallowconn AND NOT d.datistemplate ORDER BY CASE WHEN has_database_privilege(d.oid, 'CONNECT') OR pg_has_role('pg_read_all_stats', 'member') THEN pg_database_size(d.oid) END DESC NULLS LAST, d.datname
            """, ct))
        {
            var r = q.Reader;
            while (await r.ReadAsync(ct))
            {
                if (!r.IsDBNull(1))
                {
                    knownBytes += r.GetInt64(1);
                }

                databases.Add(new PgDatabaseStat(
                    r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetInt32(3),
                    r.IsDBNull(4) ? null : r.GetDouble(4), r.GetInt64(5), r.GetInt64(6)));
                hitSum += r.IsDBNull(7) ? 0 : r.GetInt64(7);
                readSum += r.IsDBNull(8) ? 0 : r.GetInt64(8);
            }
        }

        var replication = replica ? await ReadReplicaLagAsync(conn, tx, ct) : await ReadPrimaryReplicationAsync(conn, tx, ct);

        var attention = await ReadAttentionAsync(conn, tx, ct);

        return new PgOverview(
            version, replica, started, captured,
            clientBackends + 1, maxConnections, idle, idleInTxn, waiting,
            hitSum + readSum > 0 ? (double)hitSum / (hitSum + readSum) : null, sharedBuffers,
            longest,
            knownBytes, databases.Count,
            replication, xidAge, databases, attention,
            LimitedVisibility: hidden > 0 || !privileged);
    }

    private static async Task<PgReplication> ReadPrimaryReplicationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var q = await QueryAsync(conn, tx, """
            SELECT count(*), max(replay_lag), (array_agg(application_name ORDER BY replay_lag DESC NULLS LAST))[1],
                   (SELECT sum(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn))::bigint FROM pg_replication_slots)
              FROM pg_stat_replication
            """, ct);
        var r = q.Reader;
        await r.ReadAsync(ct);
        return new PgReplication(
            Int(r, 0),
            r.IsDBNull(1) ? null : r.GetTimeSpan(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetInt64(3));
    }

    private static async Task<PgReplication> ReadReplicaLagAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var q = await QueryAsync(conn, tx, "SELECT now() - pg_last_xact_replay_timestamp()", ct);
        var r = q.Reader;
        await r.ReadAsync(ct);
        return new PgReplication(0, r.IsDBNull(0) ? null : r.GetTimeSpan(0), null, null);
    }

    private static async Task<PgAttention> ReadAttentionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // Blocked sessions: how many, for how long, and which blocker holds up the most victims.
        var blocked = new List<(int Pid, TimeSpan? For, int[] Blockers)>();
        await using (var q = await QueryAsync(conn, tx, """
            SELECT a.pid, now() - a.query_start, pg_blocking_pids(a.pid)
              FROM pg_stat_activity a WHERE cardinality(pg_blocking_pids(a.pid)) > 0
            """, ct))
        {
            var r = q.Reader;
            while (await r.ReadAsync(ct))
            {
                blocked.Add((r.GetInt32(0), r.IsDBNull(1) ? null : r.GetTimeSpan(1), r.GetFieldValue<int[]>(2)));
            }
        }

        int? topBlocker = null;
        string? topRelation = null;
        if (blocked.Count > 0)
        {
            topBlocker = blocked.SelectMany(b => b.Blockers).GroupBy(p => p).OrderByDescending(g => g.Count()).First().Key;
            var victim = blocked.First(b => b.Blockers.Contains(topBlocker.Value)).Pid;
            await using var cmd = new NpgsqlCommand($"SELECT {WaitingRelationSql("@victim")}", conn, tx);
            cmd.Parameters.AddWithValue("victim", victim);
            topRelation = await cmd.ExecuteScalarAsync(ct) as string;
        }

        var idleTxns = new List<PgIdleTransaction>();
        await using (var q = await QueryAsync(conn, tx, """
            SELECT pid, coalesce(usename,''), coalesce(application_name,''), now() - state_change FROM pg_stat_activity
             WHERE state LIKE 'idle in transaction%' AND now() - state_change > interval '5 minutes' AND pid <> pg_backend_pid()
             ORDER BY state_change
            """, ct))
        {
            var r = q.Reader;
            while (await r.ReadAsync(ct))
            {
                idleTxns.Add(new PgIdleTransaction(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetTimeSpan(3)));
            }
        }

        var dead = new List<PgDeadTupleTable>();
        await using (var q = await QueryAsync(conn, tx, """
            SELECT schemaname || '.' || relname, 100.0 * n_dead_tup / nullif(n_live_tup + n_dead_tup, 0)
              FROM pg_stat_user_tables WHERE n_dead_tup > 0 AND n_live_tup + n_dead_tup >= 1000
               AND 100.0 * n_dead_tup / nullif(n_live_tup + n_dead_tup, 0) > 30 ORDER BY 2 DESC
            """, ct))
        {
            var r = q.Reader;
            while (await r.ReadAsync(ct))
            {
                dead.Add(new PgDeadTupleTable(r.GetString(0), Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture)));
            }
        }

        int unusedCount;
        long unusedBytes;
        string? largestName;
        long? largestBytes;
        await using (var q = await QueryAsync(conn, tx, """
            SELECT count(*), coalesce(sum(pg_relation_size(s.indexrelid)),0),
                   (array_agg(s.indexrelname ORDER BY pg_relation_size(s.indexrelid) DESC))[1],
                   max(pg_relation_size(s.indexrelid))
              FROM pg_stat_user_indexes s JOIN pg_index i ON i.indexrelid = s.indexrelid
             WHERE s.idx_scan = 0 AND NOT i.indisunique AND NOT i.indisprimary
            """, ct))
        {
            var r = q.Reader;
            await r.ReadAsync(ct);
            unusedCount = Int(r, 0);
            unusedBytes = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
            largestName = r.IsDBNull(2) ? null : r.GetString(2);
            largestBytes = r.IsDBNull(3) ? null : r.GetInt64(3);
        }

        DateTimeOffset? statsReset = null;
        await using (var q = await QueryAsync(conn, tx, "SELECT stats_reset FROM pg_stat_database WHERE datname = current_database()", ct))
        {
            var r = q.Reader;
            if (await r.ReadAsync(ct) && !r.IsDBNull(0))
            {
                statsReset = r.GetFieldValue<DateTimeOffset>(0);
            }
        }

        return new PgAttention(
            blocked.Count,
            blocked.Any(b => b.For is not null) ? blocked.Max(b => b.For) : null,
            topBlocker, topRelation,
            idleTxns, dead,
            unusedCount, unusedBytes, largestName, largestBytes,
            statsReset);
    }

    private static int Int(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

    /// <summary>A reader that owns its command: disposing the scope closes the reader first, then the command.</summary>
    private sealed class ReaderScope(NpgsqlCommand cmd, NpgsqlDataReader reader) : IAsyncDisposable
    {
        public NpgsqlDataReader Reader { get; } = reader;

        public async ValueTask DisposeAsync()
        {
            await Reader.DisposeAsync();
            await cmd.DisposeAsync();
        }
    }

    private static async Task<ReaderScope> QueryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        try
        {
            return new ReaderScope(cmd, await cmd.ExecuteReaderAsync(ct));
        }
        catch
        {
            await cmd.DisposeAsync();
            throw;
        }
    }
}
