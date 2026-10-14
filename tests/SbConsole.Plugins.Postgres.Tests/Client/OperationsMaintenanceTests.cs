using FluentAssertions;
using Npgsql;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Infrastructure;

namespace SbConsole.Plugins.Postgres.Tests.Client;

[Collection("postgres")]
public sealed class OperationsMaintenanceTests(PostgresFixture fx)
{
    private async Task<object?> ScalarAsync(string sql, string database = "app")
    {
        await using var conn = new NpgsqlConnection(fx.AdminConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, string database = "app") => (T)(await ScalarAsync(sql, database))!;

    private Task ExecAsync(string sql, string database = "app") => ScalarAsync(sql, database);

    private async Task WithScratchAsync(Func<string, uint, Task> body, string database = "app", int rows = 2000)
    {
        var table = "t_mnt_" + Guid.NewGuid().ToString("N");
        await ExecAsync($"CREATE TABLE {table} (id int, v text) WITH (autovacuum_enabled = false); " +
                        $"INSERT INTO {table} SELECT g, 'v' || g FROM generate_series(1, {rows}) g; " +
                        $"DELETE FROM {table} WHERE id % 2 = 0;", database);
        try
        {
            var oid = await ScalarAsync<uint>($"SELECT '{table}'::regclass::oid", database);
            await body(table, oid);
        }
        finally
        {
            await ExecAsync($"DROP TABLE IF EXISTS {table}", database);
        }
    }

    private async Task<T> PollAsync<T>(string sql, Func<T, bool> done, string database = "app")
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        T value;
        do
        {
            await ExecAsync("SELECT pg_stat_force_next_flush()", database);
            value = await ScalarAsync<T>(sql, database);
            if (done(value))
            {
                return value;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        return value;
    }

    [DockerFact]
    public async Task Maintenance_reads_tables_with_dead_tuples_and_server_settings()
    {
        await WithScratchAsync(async (table, oid) =>
        {
            var m = await PollMaintenanceAsync(table);

            m.AutovacuumOn.Should().BeTrue();
            m.MaxWorkers.Should().BeGreaterThan(0);
            m.FreezeMaxAge.Should().BeGreaterThan(0);
            m.OldestXidAge.Should().BeGreaterThanOrEqualTo(0);
            m.CapturedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            var row = m.Tables.Single(t => t.Name == table);
            row.Oid.Should().Be(oid);
            row.Schema.Should().Be("public");
            row.Dead.Should().BeGreaterThan(0);
            row.Live.Should().BeGreaterThan(0);
            row.DeadPercent.Should().BeGreaterThan(30);
            row.TotalBytes.Should().BeGreaterThan(0);
            row.AutovacuumRunning.Should().BeFalse();
            m.Tables.Select(t => t.DeadPercent).Should().BeInDescendingOrder();
        });
    }

    private async Task<PgMaintenance> PollMaintenanceAsync(string table, string database = "app")
    {
        await ExecAsync("SELECT pg_stat_force_next_flush()", database);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        PgMaintenance m;
        do
        {
            await ExecAsync("SELECT pg_stat_force_next_flush()", database);
            m = await fx.Operations.GetMaintenanceAsync(fx.Secret(PgAccessMode.ReadOnly, database), database);
            if (m.Tables.Any(t => t.Name == table && t.Dead > 0))
            {
                break;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        return m;
    }

    [DockerFact]
    public async Task Without_pgstattuple_bloat_is_null_and_the_extension_is_reported_absent()
    {
        await WithScratchAsync(async (table, _) =>
        {
            var m = await PollMaintenanceAsync(table);

            m.PgstattupleAvailable.Should().BeFalse();
            m.Tables.Should().OnlyContain(t => t.BloatBytes == null);
        });
    }

    [DockerFact]
    public async Task With_pgstattuple_bloat_is_estimated()
    {
        var db = "mnt_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync($"CREATE DATABASE {db}", "postgres");
        try
        {
            await ExecAsync("CREATE EXTENSION pgstattuple", db);
            await WithScratchAsync(async (table, _) =>
            {
                var m = await PollMaintenanceAsync(table, db);

                m.PgstattupleAvailable.Should().BeTrue();
                m.Tables.Single(t => t.Name == table).BloatBytes.Should().BeGreaterThan(0);
            }, db, rows: 20000);
        }
        finally
        {
            await ExecAsync($"DROP DATABASE IF EXISTS {db} WITH (FORCE)", "postgres");
        }
    }

    [DockerFact]
    public async Task Vacuum_reduces_dead_tuples_and_returns_the_qualified_name()
    {
        await WithScratchAsync(async (table, oid) =>
        {
            var name = await fx.Operations.RunMaintenanceAsync(fx.Secret(PgAccessMode.ReadWrite), "app", oid, PgMaintenanceKind.Vacuum);

            name.Should().Be($"\"public\".\"{table}\"");
            var dead = await PollAsync<long>($"SELECT n_dead_tup FROM pg_stat_user_tables WHERE relid = {oid}", d => d == 0);
            dead.Should().Be(0);
        });
    }

    [DockerFact]
    public async Task Analyze_sets_last_analyze()
    {
        await WithScratchAsync(async (table, oid) =>
        {
            await fx.Operations.RunMaintenanceAsync(fx.Secret(PgAccessMode.ReadWrite), "app", oid, PgMaintenanceKind.Analyze);

            var analyzed = await PollAsync<bool>($"SELECT last_analyze IS NOT NULL FROM pg_stat_user_tables WHERE relid = {oid}", b => b);
            analyzed.Should().BeTrue();
        });
    }

    [DockerFact]
    public async Task Vacuum_full_rewrites_the_table()
    {
        await WithScratchAsync(async (table, oid) =>
        {
            var before = await ScalarAsync<long>($"SELECT pg_relation_size('{table}')");

            var name = await fx.Operations.RunMaintenanceAsync(fx.Secret(PgAccessMode.ReadWrite), "app", oid, PgMaintenanceKind.VacuumFull);

            name.Should().Be($"\"public\".\"{table}\"");
            (await ScalarAsync<long>($"SELECT pg_relation_size('{table}')")).Should().BeLessThan(before);
        });
    }

    [DockerFact]
    public async Task Read_only_connections_are_refused_before_connecting()
    {
        var unreachable = new PostgresConnectionSettings("127.0.0.1", 1, "app", "postgres", "postgres", SslMode: "Disable").ToSecretString();

        var act = () => fx.Operations.RunMaintenanceAsync(unreachable, "app", 1, PgMaintenanceKind.Vacuum);

        await act.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("*read-only*Maintenance*");
    }

    [DockerFact]
    public async Task A_dropped_or_non_table_oid_is_refused()
    {
        await WithScratchAsync(async (table, _) =>
        {
            await ExecAsync($"CREATE INDEX {table}_i ON {table} (id)");
            var indexOid = await ScalarAsync<uint>($"SELECT '{table}_i'::regclass::oid");

            var missing = () => fx.Operations.RunMaintenanceAsync(fx.Secret(PgAccessMode.ReadWrite), "app", uint.MaxValue - 1, PgMaintenanceKind.Vacuum);
            var index = () => fx.Operations.RunMaintenanceAsync(fx.Secret(PgAccessMode.ReadWrite), "app", indexOid, PgMaintenanceKind.Analyze);

            await missing.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("That table no longer exists.");
            await index.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("That table no longer exists.");
        });
    }
}
