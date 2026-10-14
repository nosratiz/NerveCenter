using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    private const int BloatTableLimit = 25;

    public Task<PgMaintenance> GetMaintenanceAsync(string secret, string? database, CancellationToken ct = default) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            bool autovacuumOn;
            int maxWorkers;
            long freezeMaxAge, oldestXidAge;
            bool pgstattuple;
            DateTimeOffset now;
            await using (var cmd = new NpgsqlCommand(
                "SELECT current_setting('autovacuum') = 'on', current_setting('autovacuum_max_workers')::int, " +
                "current_setting('autovacuum_freeze_max_age')::bigint, (SELECT max(age(datfrozenxid)) FROM pg_database)::bigint, " +
                "EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgstattuple'), now()", conn, tx))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                autovacuumOn = r.GetBoolean(0);
                maxWorkers = r.GetInt32(1);
                freezeMaxAge = r.GetInt64(2);
                oldestXidAge = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                pgstattuple = r.GetBoolean(4);
                now = r.GetFieldValue<DateTimeOffset>(5);
            }

            var running = new List<PgVacuumProgress>();
            var runningOids = new HashSet<uint>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT p.pid, p.relid::regclass::text, a.xact_start, " +
                "CASE WHEN p.heap_blks_total > 0 THEN 100.0 * p.heap_blks_scanned / p.heap_blks_total END, p.relid " +
                "FROM pg_stat_progress_vacuum p LEFT JOIN pg_stat_activity a ON a.pid = p.pid " +
                "WHERE p.datname = current_database() ORDER BY p.pid", conn, tx))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    running.Add(new PgVacuumProgress(r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
                        r.IsDBNull(3) ? null : Convert.ToDouble(r.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)));
                    runningOids.Add(r.GetFieldValue<uint>(4));
                }
            }

            var tables = new List<PgTableHealth>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT s.relid, s.schemaname, s.relname, pg_total_relation_size(s.relid), s.n_live_tup, s.n_dead_tup, " +
                "coalesce(100.0 * s.n_dead_tup / nullif(s.n_live_tup + s.n_dead_tup, 0), 0)::float8, " +
                "s.last_vacuum, s.last_autovacuum, s.last_analyze, s.last_autoanalyze " +
                "FROM pg_stat_user_tables s ORDER BY 7 DESC, 4 DESC", conn, tx))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var oid = r.GetFieldValue<uint>(0);
                    tables.Add(new PgTableHealth(oid, r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                        r.GetDouble(6), null, Ts(r, 7), Ts(r, 8), Ts(r, 9), Ts(r, 10), runningOids.Contains(oid)));
                }
            }

            if (pgstattuple)
            {
                for (var i = 0; i < Math.Min(BloatTableLimit, tables.Count); i++)
                {
                    tables[i] = tables[i] with { BloatBytes = await BloatAsync(conn, tx, tables[i].Oid, ct) };
                }
            }

            return new PgMaintenance(autovacuumOn, maxWorkers, running, oldestXidAge, freezeMaxAge, pgstattuple, tables, now);
        }, ct);

    private static DateTimeOffset? Ts(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    // Each call runs inside a SAVEPOINT so one table's failure (permissions, a dropped relation) does not abort the
    // read transaction; that table simply has no estimate. Read-only transactions allow savepoints.
    private static async Task<long?> BloatAsync(NpgsqlConnection conn, NpgsqlTransaction tx, uint oid, CancellationToken ct)
    {
        await using (var save = new NpgsqlCommand("SAVEPOINT pg_bloat", conn, tx))
        {
            await save.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT (dead_tuple_len + approx_free_space)::bigint FROM pgstattuple_approx(@oid::regclass)", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("oid", NpgsqlTypes.NpgsqlDbType.Oid) { Value = oid });
            var value = await cmd.ExecuteScalarAsync(ct);
            await using var release = new NpgsqlCommand("RELEASE SAVEPOINT pg_bloat", conn, tx);
            await release.ExecuteNonQueryAsync(ct);
            return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (PostgresException)
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT pg_bloat", conn, tx);
            await rollback.ExecuteNonQueryAsync(ct);
            return null;
        }
    }

    // VACUUM cannot run inside a transaction block, so this uses a plain autocommit connection (not ReadAsync).
    // The statement text is a literal per kind; the table name is resolved from the catalog by oid and quoted.
    public async Task<string> RunMaintenanceAsync(string secret, string? database, uint tableOid, PgMaintenanceKind kind, CancellationToken ct = default)
    {
        var settings = PostgresConnectionSettings.Parse(secret);
        if (settings.Mode != PgAccessMode.ReadWrite)
        {
            throw new PgSqlRefusedException("This connection is read-only — set Mode to Read-write in Connections to run Maintenance.");
        }

        await using var conn = Open(settings, database);
        await conn.OpenAsync(ct);

        string qualified;
        await using (var find = new NpgsqlCommand(
            "SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE c.oid = @oid AND c.relkind IN ('r', 'm', 'p')", conn))
        {
            find.Parameters.Add(new NpgsqlParameter("oid", NpgsqlTypes.NpgsqlDbType.Oid) { Value = tableOid });
            await using var r = await find.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                throw new PgSqlRefusedException("That table no longer exists.");
            }

            qualified = PgIdent.Qualified(r.GetString(0), r.GetString(1));
        }

        var (verb, timeoutS) = kind switch
        {
            PgMaintenanceKind.Vacuum => ("VACUUM (VERBOSE false)", Math.Clamp(settings.StatementTimeoutS, 1, PostgresConnectionSettings.MaxTimeoutS)),
            PgMaintenanceKind.Analyze => ("ANALYZE", Math.Clamp(settings.StatementTimeoutS, 1, PostgresConnectionSettings.MaxTimeoutS)),
            PgMaintenanceKind.VacuumFull => ("VACUUM FULL", 3600),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // timeoutS is an int clamped above, formatted here -- never user text.
        await using (var set = new NpgsqlCommand($"SET statement_timeout = '{timeoutS.ToString(System.Globalization.CultureInfo.InvariantCulture)}s'", conn))
        {
            await set.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand($"{verb} {qualified}", conn) { CommandTimeout = timeoutS + 5 };
        await cmd.ExecuteNonQueryAsync(ct);
        return qualified;
    }
}
