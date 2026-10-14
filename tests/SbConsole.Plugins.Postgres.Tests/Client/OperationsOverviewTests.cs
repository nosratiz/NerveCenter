using FluentAssertions;
using Npgsql;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Infrastructure;

namespace SbConsole.Plugins.Postgres.Tests.Client;

[Collection("postgres")]
public sealed class OperationsOverviewTests(PostgresFixture fx)
{
    private static async Task<NpgsqlConnection> OpenAsync(string cs)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<PgOverview> PollAsync(Func<PgOverview, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        PgOverview o;
        do
        {
            o = await fx.Operations.GetOverviewAsync(fx.Secret());
            if (until(o)) return o;
            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);
        return o;
    }

    [DockerFact]
    public async Task Test_connection_reports_version_and_role()
    {
        var result = await fx.Operations.TestConnectionAsync(fx.Secret());

        result.Success.Should().BeTrue();
        result.Identity.Should().Contain("PostgreSQL 16").And.Contain("primary");
    }

    [DockerFact]
    public async Task Test_connection_with_wrong_password_fails_friendly()
    {
        var result = await fx.Operations.TestConnectionAsync(fx.SecretWithPassword("nope"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Password authentication failed.");
    }

    [DockerFact]
    public async Task Lists_connectable_databases()
    {
        var dbs = await fx.Operations.ListDatabasesAsync(fx.Secret());

        dbs.Should().Contain(["app", "analytics", "postgres"]);
        dbs.Should().NotContain("template0");
    }

    [DockerFact]
    public async Task Overview_reads_server_and_database_stats()
    {
        var o = await fx.Operations.GetOverviewAsync(fx.Secret());

        o.MaxConnections.Should().BeGreaterThan(0);
        o.Connections.Should().BeGreaterThan(0);
        o.IsReplica.Should().BeFalse();
        o.ServerVersion.Should().StartWith("16");
        o.Databases.Should().Contain(d => d.Name == "app" && d.Bytes > 0);
        o.DatabaseCount.Should().Be(o.Databases.Count);
        o.Replication.ReplicaCount.Should().Be(0);
        o.OldestXidAge.Should().BeGreaterThan(0);
    }

    [DockerFact]
    public async Task Overview_counts_an_idle_in_transaction_session()
    {
        await using var conn = await OpenAsync(fx.AdminConnectionString());
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await ExecAsync(conn, "SELECT 1");
            var pid = conn.ProcessID;

            var o = await PollAsync(x => x.IdleInTransaction >= 1);

            o.IdleInTransaction.Should().BeGreaterThanOrEqualTo(1);
            o.LongestTransaction.Should().NotBeNull();
            o.LongestTransaction!.Pid.Should().Be(pid);
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [DockerFact]
    public async Task Overview_sees_a_blocked_session()
    {
        await using var a = await OpenAsync(fx.AdminConnectionString());
        await using var txA = await a.BeginTransactionAsync();
        using var cts = new CancellationTokenSource();
        Task? blocked = null;
        try
        {
            await ExecAsync(a, "UPDATE orders SET status = 'x' WHERE id = 1");
            var blockerPid = a.ProcessID;

            blocked = Task.Run(async () =>
            {
                await using var b = await OpenAsync(fx.AdminConnectionString());
                // Explicit transaction, always rolled back: B can never commit a change to the shared seed row.
                await using var txB = await b.BeginTransactionAsync();
                try
                {
                    await using var cmd = new NpgsqlCommand("UPDATE orders SET status = 'y' WHERE id = 1", b, txB);
                    await cmd.ExecuteNonQueryAsync(cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (PostgresException) { }
                finally
                {
                    try { await txB.RollbackAsync(); } catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { }
                }
            });

            var o = await PollAsync(x => x.Attention.BlockedCount == 1);

            o.Attention.BlockedCount.Should().Be(1);
            o.Attention.TopBlockerPid.Should().Be(blockerPid);
            o.Attention.TopBlockerRelation.Should().Contain("orders");
            o.Attention.LongestBlockedFor.Should().NotBeNull();
            (await fx.Operations.GetBlockedCountAsync(fx.Secret())).Should().Be(1);
        }
        finally
        {
            await cts.CancelAsync();
            if (blocked is not null) await blocked;
            await txA.RollbackAsync();
        }
    }

    [DockerFact]
    public async Task Overview_flags_unused_indexes()
    {
        var o = await fx.Operations.GetOverviewAsync(fx.Secret());

        o.Attention.UnusedIndexCount.Should().BeGreaterThanOrEqualTo(1);
        o.Attention.UnusedIndexBytes.Should().BeGreaterThan(0);
        o.Attention.LargestUnusedIndex.Should().Be("idx_orders_legacy");
    }

    [DockerFact]
    public async Task Overview_tolerates_a_database_the_role_cannot_connect_to()
    {
        var id = Guid.NewGuid().ToString("N");
        var db = "restricted_" + id;
        var role = "limited_" + id;
        const string password = "limited-pw-1";
        await using var admin = await OpenAsync(fx.AdminConnectionString("postgres"));
        try
        {
            await ExecAsync(admin, $"CREATE DATABASE {db}");
            await ExecAsync(admin, $"REVOKE CONNECT ON DATABASE {db} FROM PUBLIC");
            await ExecAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER");

            var o = await fx.Operations.GetOverviewAsync(fx.SecretForRole(role, password));

            o.Databases.Should().Contain(d => d.Name == db && d.Bytes == null);
            o.Databases.Should().Contain(d => d.Name == "app" && d.Bytes > 0);
        }
        finally
        {
            await ExecAsync(admin, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
            await ExecAsync(admin, $"DROP ROLE IF EXISTS {role}");
        }
    }

    private async Task<(string Role, string Password)> CreateLimitedRoleAsync(NpgsqlConnection admin)
    {
        var role = "limited_" + Guid.NewGuid().ToString("N");
        const string password = "limited-pw-1";
        await ExecAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER");
        return (role, password);
    }

    [DockerFact]
    public async Task A_limited_role_still_counts_other_users_connections()
    {
        await using var admin = await OpenAsync(fx.AdminConnectionString("postgres"));
        (string Role, string Password) limited = default;
        try
        {
            limited = await CreateLimitedRoleAsync(admin);
            await using var other1 = await OpenAsync(fx.AdminConnectionString());
            await using var other2 = await OpenAsync(fx.AdminConnectionString());
            await using var other3 = await OpenAsync(fx.AdminConnectionString());

            var o = await fx.Operations.GetOverviewAsync(fx.SecretForRole(limited.Role, limited.Password));

            // Three other-user sessions plus this overview's own connection; their backend_type / state are hidden from the role.
            o.Connections.Should().BeGreaterThanOrEqualTo(4);
            o.LimitedVisibility.Should().BeTrue();
        }
        finally
        {
            if (limited.Role is not null) await ExecAsync(admin, $"DROP ROLE IF EXISTS {limited.Role}");
        }
    }

    [DockerFact]
    public async Task A_superuser_has_full_visibility()
    {
        var o = await fx.Operations.GetOverviewAsync(fx.Secret());

        o.LimitedVisibility.Should().BeFalse();
    }

    [DockerFact]
    public async Task A_role_with_pg_monitor_has_full_visibility()
    {
        await using var admin = await OpenAsync(fx.AdminConnectionString("postgres"));
        (string Role, string Password) monitor = default;
        try
        {
            monitor = await CreateLimitedRoleAsync(admin);
            await ExecAsync(admin, $"GRANT pg_monitor TO {monitor.Role}");
            await using var other = await OpenAsync(fx.AdminConnectionString());

            var o = await fx.Operations.GetOverviewAsync(fx.SecretForRole(monitor.Role, monitor.Password));

            o.LimitedVisibility.Should().BeFalse();
            o.Connections.Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            if (monitor.Role is not null) await ExecAsync(admin, $"DROP ROLE IF EXISTS {monitor.Role}");
        }
    }

    [DockerFact]
    public async Task Blocked_duration_is_unknown_not_zero_when_query_start_is_hidden()
    {
        await using var admin = await OpenAsync(fx.AdminConnectionString("postgres"));
        await using var a = await OpenAsync(fx.AdminConnectionString());
        await using var txA = await a.BeginTransactionAsync();
        using var cts = new CancellationTokenSource();
        Task? blocked = null;
        (string Role, string Password) limited = default;
        try
        {
            limited = await CreateLimitedRoleAsync(admin);
            await ExecAsync(a, "UPDATE orders SET status = 'x' WHERE id = 2");
            blocked = Task.Run(async () =>
            {
                await using var b = await OpenAsync(fx.AdminConnectionString());
                await using var txB = await b.BeginTransactionAsync();
                try
                {
                    await using var cmd = new NpgsqlCommand("UPDATE orders SET status = 'y' WHERE id = 2", b, txB);
                    await cmd.ExecuteNonQueryAsync(cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (PostgresException) { }
                finally
                {
                    try { await txB.RollbackAsync(); } catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { }
                }
            });

            var secret = fx.SecretForRole(limited.Role, limited.Password);
            PgOverview o;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                o = await fx.Operations.GetOverviewAsync(secret);
                if (o.Attention.BlockedCount >= 1) break;
                await Task.Delay(200);
            }
            while (DateTime.UtcNow < deadline);

            o.Attention.BlockedCount.Should().BeGreaterThanOrEqualTo(1);
            o.Attention.LongestBlockedFor.Should().BeNull();
        }
        finally
        {
            await cts.CancelAsync();
            if (blocked is not null) await blocked;
            await txA.RollbackAsync();
            if (limited.Role is not null) await ExecAsync(admin, $"DROP ROLE IF EXISTS {limited.Role}");
        }
    }

    [DockerFact]
    public async Task Overview_flags_tables_over_30_percent_dead()
    {
        var table = "t_dead_" + Guid.NewGuid().ToString("N");
        await using var conn = await OpenAsync(fx.AdminConnectionString());
        try
        {
            await ExecAsync(conn, $"CREATE TABLE {table} (id int) WITH (autovacuum_enabled = false)");
            await ExecAsync(conn, $"INSERT INTO {table} SELECT generate_series(1, 2000)");
            await ExecAsync(conn, $"DELETE FROM {table} WHERE id <= 1000");
            await ScalarAsync<object>(conn, "SELECT pg_stat_force_next_flush()::text");

            var o = await PollAsync(x => x.Attention.DeadTupleTables.Any(t => t.Name.EndsWith(table)));

            o.Attention.DeadTupleTables.Should().Contain(t => t.Name == "public." + table && t.DeadPercent > 30);
        }
        finally
        {
            await ExecAsync(conn, $"DROP TABLE IF EXISTS {table}");
        }
    }
}
