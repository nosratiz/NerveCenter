using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Query;

public class RunQueryCommandHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    public RunQueryCommandHandlerTests() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private RunQueryCommandHandler Sut() => new(_ops, _connections, _audit, _store, _clock, NullLogger<RunQueryCommandHandler>.Instance);

    private static RunQueryCommand Cmd(string sql = "select * from orders", bool write = false, string? db = "app") =>
        new(Id, "pg-prod-eu", db, new PgQueryRequest(sql, write, 1000, 30), "nima");

    private static PgQueryResult Result(long? affected = null, int rows = 3) =>
        new([], Enumerable.Range(0, rows).Select(_ => (IReadOnlyList<PgCell>)[]).ToList(), false, affected, TimeSpan.FromMilliseconds(1230), 1, null);

    private async Task<IReadOnlyList<QueryHistoryEntry>> History() => await QueryHistoryStore.ReadAsync(_store, Id, default);

    [Fact]
    public async Task Missing_connection_fails_without_audit_or_history()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Sut().HandleAsync(Cmd());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
        (await History()).Should().BeEmpty();
    }

    [Fact]
    public async Task Refused_sql_returns_the_error_and_leaves_no_trace()
    {
        _ops.RunQueryAsync("secret", "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PgSqlRefusedException("Not allowed."));

        var result = await Sut().HandleAsync(Cmd("drop table x"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Result.Should().BeNull();
        result.Value.Error!.Message.Should().Be("Not allowed.");
        result.Value.AuditAction.Should().Be("pg.query.read");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
        (await History()).Should().BeEmpty();
    }

    [Fact]
    public async Task Read_success_audits_safe_and_records_history()
    {
        var cmd = Cmd();
        _ops.RunQueryAsync("secret", "app", cmd.Request, Arg.Any<CancellationToken>()).Returns(Result());

        var result = await Sut().HandleAsync(cmd);

        var hash = QueryAudit.Hash(cmd.Request.Sql);
        result.Value!.Result.Should().NotBeNull();
        result.Value.AuditAction.Should().Be(RunQueryCommandHandler.ReadAction);
        result.Value.Hash.Should().Be(hash);
        await _audit.Received(1).RecordAsync("pg.query.read", "pg-prod-eu/app", ActionRisk.Safe, true,
            $"{hash} · select * from orders · 3 rows · 1.23 s", Arg.Any<CancellationToken>());
        var h = (await History()).Single();
        h.Should().Match<QueryHistoryEntry>(e => e.Rows == 3 && !e.Write && e.SqlState == null && e.Actor == "nima"
            && e.Hash == hash && e.Database == "app" && e.Sql == cmd.Request.Sql);
        h.ElapsedSeconds.Should().BeApproximately(1.23, 0.001);
    }

    [Fact]
    public async Task Default_database_target_is_marked()
    {
        _ops.RunQueryAsync("secret", null, Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns(Result());

        await Sut().HandleAsync(Cmd(db: null));

        await _audit.Received(1).RecordAsync(Arg.Any<string>(), "pg-prod-eu/(default)", Arg.Any<ActionRisk>(), true,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Write_success_audits_mutating_with_rows_affected()
    {
        var cmd = Cmd("update orders set a=1", write: true);
        _ops.RunQueryAsync("secret", "app", cmd.Request, Arg.Any<CancellationToken>()).Returns(Result(affected: 7, rows: 0));

        var result = await Sut().HandleAsync(cmd);

        result.Value!.AuditAction.Should().Be("pg.query.write");
        await _audit.Received(1).RecordAsync("pg.query.write", "pg-prod-eu/app", ActionRisk.Mutating, true,
            $"{QueryAudit.Hash(cmd.Request.Sql)} · update orders set a=1 · 7 rows · 1.23 s", Arg.Any<CancellationToken>());
        var h = (await History()).Single();
        h.Write.Should().BeTrue();
        h.Rows.Should().Be(7);
    }

    [Fact]
    public async Task Server_error_is_audited_as_failed_with_sqlstate_only_and_returns_error_info()
    {
        var cmd = Cmd("select\n  nope from orders");
        var pg = new Npgsql.PostgresException("column \"nope\" does not exist", "ERROR", "ERROR", "42703", null, null, 10);
        _ops.RunQueryAsync("secret", "app", cmd.Request, Arg.Any<CancellationToken>())
            .Returns<Task<PgQueryResult>>(_ => { _clock.Advance(TimeSpan.FromSeconds(1.5)); throw pg; });

        var result = await Sut().HandleAsync(cmd);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Result.Should().BeNull();
        result.Value.Error!.SqlState.Should().Be("42703");
        result.Value.Error.Line.Should().Be(2);
        await _audit.Received(1).RecordAsync("pg.query.read", "pg-prod-eu/app", ActionRisk.Safe, false,
            $"{QueryAudit.Hash(cmd.Request.Sql)} · select nope from orders · SQLSTATE 42703", Arg.Any<CancellationToken>());
        var h = (await History()).Single();
        h.SqlState.Should().Be("42703");
        h.ElapsedSeconds.Should().BeApproximately(1.5, 0.001);
    }

    [Fact]
    public async Task Connection_failure_is_audited_as_failed_too()
    {
        _ops.RunQueryAsync("secret", "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Npgsql.NpgsqlException("boom", new TimeoutException()));

        var result = await Sut().HandleAsync(Cmd());

        result.Value!.Error!.IsConnectionFailure.Should().BeTrue();
        await _audit.Received(1).RecordAsync("pg.query.read", Arg.Any<string>(), ActionRisk.Safe, false,
            Arg.Is<string?>(d => d!.Contains("connection failed")), Arg.Any<CancellationToken>());
        (await History()).Should().ContainSingle();
    }

    [Fact]
    public async Task Caller_cancellation_is_audited_and_recorded_then_rethrown()
    {
        using var cts = new CancellationTokenSource();
        var cmd = Cmd("update orders set a=1", write: true);
        _ops.RunQueryAsync("secret", "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<PgQueryResult>>(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });

        var act = () => Sut().HandleAsync(cmd, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var hash = QueryAudit.Hash(cmd.Request.Sql);
        await _audit.Received(1).RecordAsync("pg.query.write", "pg-prod-eu/app", ActionRisk.Mutating, false,
            $"{hash} · update orders set a=1 · cancelled", CancellationToken.None);
        var h = (await History()).Single();
        h.Should().Match<QueryHistoryEntry>(e => e.SqlState == null && e.Rows == null && e.Write && e.Hash == hash);
    }

    [Fact]
    public async Task Audit_and_history_write_failures_do_not_fail_the_run()
    {
        _ops.RunQueryAsync("secret", "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns(Result());
        _audit.RecordAsync(default!, default!, default, default, default, default).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("db")));
        var store = Substitute.For<IPluginStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<string?>(new InvalidOperationException("x")));
        var sut = new RunQueryCommandHandler(_ops, _connections, _audit, store, _clock, NullLogger<RunQueryCommandHandler>.Instance);

        var result = await sut.HandleAsync(Cmd());

        result.Value!.Result.Should().NotBeNull();
    }
}
