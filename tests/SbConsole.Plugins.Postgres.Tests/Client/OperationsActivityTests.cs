using FluentAssertions;
using Npgsql;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Infrastructure;

namespace SbConsole.Plugins.Postgres.Tests.Client;

[Collection("postgres")]
public sealed class OperationsActivityTests(PostgresFixture fx)
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

    private async Task<PgBackend?> PollAsync(int pid, Func<PgBackend, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        PgBackend? b;
        do
        {
            b = await fx.Operations.GetBackendAsync(fx.Secret(), pid);
            if (b is not null && until(b)) return b;
            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);
        return b;
    }

    [DockerFact]
    public async Task Lists_an_idle_in_transaction_session_and_marks_own_pid()
    {
        await using var conn = await OpenAsync(fx.AdminConnectionString());
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await ExecAsync(conn, "SELECT 4242");
            var pid = conn.ProcessID;

            var b = await PollAsync(pid, x => x.State == "idle in transaction");
            var activity = await fx.Operations.GetActivityAsync(fx.Secret());

            b.Should().NotBeNull();
            b!.State.Should().Be("idle in transaction");
            b.Query.Should().Contain("4242");
            b.XactStart.Should().NotBeNull();
            b.Duration.Should().NotBeNull();
            activity.Backends.Should().Contain(x => x.Pid == pid);
            activity.OwnPid.Should().BeGreaterThan(0).And.NotBe(pid);
            activity.Backends.Should().Contain(x => x.Pid == activity.OwnPid);
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [DockerFact]
    public async Task Reports_blocker_and_waited_on_relation()
    {
        var table = "t_act_" + Guid.NewGuid().ToString("N");
        await using var setup = await OpenAsync(fx.AdminConnectionString());
        await ExecAsync(setup, $"CREATE TABLE {table} (id int primary key, v text)");
        await ExecAsync(setup, $"INSERT INTO {table} VALUES (1, 'a')");
        await using var a = await OpenAsync(fx.AdminConnectionString());
        await using var txA = await a.BeginTransactionAsync();
        using var cts = new CancellationTokenSource();
        Task? waiter = null;
        try
        {
            await ExecAsync(a, $"UPDATE {table} SET v = 'x' WHERE id = 1");
            var blockerPid = a.ProcessID;
            var bPid = new TaskCompletionSource<int>();
            waiter = Task.Run(async () =>
            {
                await using var b = await OpenAsync(fx.AdminConnectionString());
                bPid.SetResult(b.ProcessID);
                await using var txB = await b.BeginTransactionAsync();
                try
                {
                    await using var cmd = new NpgsqlCommand($"UPDATE {table} SET v = 'y' WHERE id = 1", b, txB);
                    await cmd.ExecuteNonQueryAsync(cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (PostgresException) { }
                finally
                {
                    try { await txB.RollbackAsync(); } catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException) { }
                }
            });
            var victim = await bPid.Task;

            var v = await PollAsync(victim, x => x.BlockedBy.Count > 0 && x.WaitingOnRelation is not null);

            v.Should().NotBeNull();
            v!.BlockedBy.Should().Contain(blockerPid);
            v.WaitingOnRelation.Should().Be(table);
        }
        finally
        {
            await cts.CancelAsync();
            if (waiter is not null) await waiter;
            await txA.RollbackAsync();
            await ExecAsync(setup, $"DROP TABLE IF EXISTS {table}");
        }
    }

    [DockerFact]
    public async Task GetBackend_returns_null_for_a_dead_pid()
    {
        (await fx.Operations.GetBackendAsync(fx.Secret(), int.MaxValue)).Should().BeNull();
    }

    [DockerFact]
    public async Task Cancel_stops_a_running_statement()
    {
        await using var x = await OpenAsync(fx.AdminConnectionString());
        var pid = x.ProcessID;
        var running = Task.Run(async () =>
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_sleep(30)", x);
            await cmd.ExecuteNonQueryAsync();
        });
        try
        {
            var b = await PollAsync(pid, s => s.State == "active" && s.Query.Contains("pg_sleep"));
            b.Should().NotBeNull();
            b!.State.Should().Be("active");

            (await fx.Operations.CancelBackendAsync(fx.Secret(PgAccessMode.ReadWrite), pid)).Should().BeTrue();

            var ex = await FluentActions.Awaiting(() => running.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().ThrowAsync<PostgresException>();
            ex.Which.SqlState.Should().Be("57014");
        }
        finally
        {
            try { await running; } catch (Exception e) when (e is PostgresException or NpgsqlException) { }
        }
    }

    [DockerFact]
    public async Task Terminate_kills_an_idle_session()
    {
        await using var y = await OpenAsync(fx.AdminConnectionString());
        var pid = y.ProcessID;
        await ExecAsync(y, "SELECT 1");

        (await fx.Operations.TerminateBackendAsync(fx.Secret(PgAccessMode.ReadWrite), pid)).Should().BeTrue();

        var act = () => ExecAsync(y, "SELECT 1");
        var ex = await act.Should().ThrowAsync<Exception>();
        (ex.Which is NpgsqlException || ex.Which is PostgresException { SqlState: "57P01" }).Should().BeTrue();
    }

    [DockerFact]
    public async Task Cancel_and_terminate_are_refused_on_a_read_only_connection()
    {
        var cancel = () => fx.Operations.CancelBackendAsync(fx.Secret(), 1);
        var terminate = () => fx.Operations.TerminateBackendAsync(fx.Secret(), 1);

        await cancel.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("*read-only*");
        await terminate.Should().ThrowAsync<PgSqlRefusedException>().WithMessage("*read-only*");
    }
}
