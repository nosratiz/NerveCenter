using FluentAssertions;
using Npgsql;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Infrastructure;

namespace SbConsole.Plugins.Postgres.Tests.Client;

[Collection("postgres")]
public sealed class OperationsIndexTests(PostgresFixture fx)
{
    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.AdminConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql) => _ = await ScalarOrNullAsync(sql);

    private async Task<object?> ScalarOrNullAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fx.AdminConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task WithScratchAsync(string setup, Func<string, Task> body)
    {
        var table = "t_idx_" + Guid.NewGuid().ToString("N");
        await ExecAsync($"CREATE TABLE {table} (id int, v text); INSERT INTO {table} SELECT g, 'v' || g FROM generate_series(1, 50) g; {setup.Replace("{t}", table)}");
        try
        {
            await body(table);
        }
        finally
        {
            await ExecAsync($"DROP TABLE IF EXISTS {table}");
        }
    }

    private Task<uint> IndexOid(string name) => ScalarAsync<uint>($"SELECT '{name}'::regclass::oid");

    [DockerFact]
    public async Task Reindex_rebuilds_the_index_and_returns_its_qualified_name()
    {
        await WithScratchAsync("CREATE INDEX {t}_v ON {t} (v);", async table =>
        {
            var oid = await IndexOid($"{table}_v");
            var before = await ScalarAsync<uint>($"SELECT relfilenode FROM pg_class WHERE relname = '{table}_v'");

            var name = await fx.Operations.ReindexIndexAsync(fx.Secret(PgAccessMode.ReadWrite), "app", oid);

            name.Should().Be($"\"public\".\"{table}_v\"");
            // CONCURRENTLY swaps in a new index, so look it up by name rather than by the old oid.
            (await ScalarAsync<uint>($"SELECT relfilenode FROM pg_class WHERE relname = '{table}_v'")).Should().NotBe(before);
        });
    }

    [DockerFact]
    public async Task Drop_removes_the_index()
    {
        await WithScratchAsync("CREATE INDEX {t}_v ON {t} (v);", async table =>
        {
            var oid = await IndexOid($"{table}_v");

            var name = await fx.Operations.DropIndexAsync(fx.Secret(PgAccessMode.ReadWrite), "app", oid);

            name.Should().Be($"\"public\".\"{table}_v\"");
            (await ScalarAsync<long>($"SELECT count(*) FROM pg_class WHERE oid = {oid}")).Should().Be(0);
        });
    }

    [DockerFact]
    public async Task Drop_refuses_a_constraint_backed_index()
    {
        await WithScratchAsync("ALTER TABLE {t} ADD CONSTRAINT {t}_pk PRIMARY KEY (id);", async table =>
        {
            var oid = await IndexOid($"{table}_pk");

            var act = () => fx.Operations.DropIndexAsync(fx.Secret(PgAccessMode.ReadWrite), "app", oid);

            await act.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("*constraint*");
            (await ScalarAsync<long>($"SELECT count(*) FROM pg_class WHERE oid = {oid}")).Should().Be(1);
        });
    }

    [DockerFact]
    public async Task Both_refuse_a_missing_index_and_a_non_index_oid()
    {
        await WithScratchAsync("", async table =>
        {
            var tableOid = await ScalarAsync<uint>($"SELECT '{table}'::regclass::oid");

            var reindexMissing = () => fx.Operations.ReindexIndexAsync(fx.Secret(PgAccessMode.ReadWrite), "app", uint.MaxValue - 1);
            var dropTable = () => fx.Operations.DropIndexAsync(fx.Secret(PgAccessMode.ReadWrite), "app", tableOid);

            await reindexMissing.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("*no longer exists*");
            await dropTable.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("*no longer exists*");
        });
    }

    [DockerFact]
    public async Task Read_only_connections_are_refused_before_connecting()
    {
        var unreachable = new PostgresConnectionSettings("127.0.0.1", 1, "app", "postgres", "postgres", SslMode: "Disable").ToSecretString();

        var reindex = () => fx.Operations.ReindexIndexAsync(unreachable, "app", 1);
        var drop = () => fx.Operations.DropIndexAsync(unreachable, "app", 1);

        await reindex.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("*read-only*");
        await drop.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("*read-only*");
    }
}
