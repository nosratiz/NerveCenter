using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Query;

/// <summary>Run / Explain / Dry run: queries, never audited (design spec §11.3, §11.4).</summary>
public class QueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db;Mode=ReadWrite";
    private const string Sql = "DELETE FROM events WHERE tenant_id = 7";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _unknownId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    public QueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _connections.GetSecretAsync(_unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);
    }

    private RunQueryQueryHandler Run() => new(_operations, _connections, NullLogger<RunQueryQueryHandler>.Instance);

    private ExplainQueryQueryHandler Explain() => new(_operations, _connections, NullLogger<ExplainQueryQueryHandler>.Instance);

    private DryRunWriteQueryHandler DryRun() => new(_operations, _connections, NullLogger<DryRunWriteQueryHandler>.Instance);

    [Fact]
    public async Task Run_uses_the_default_limit()
    {
        var page = new QueryResult([new QueryColumn("n", null, false)], [], 0, false, 1, 1, TimeSpan.Zero);
        _operations.RunQueryAsync(Secret, "SELECT 1", QueryResult.DefaultLimit, null, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Run().HandleAsync(_connectionId, "SELECT 1");

        result.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task Run_passes_a_larger_limit_for_load_more()
    {
        var page = new QueryResult([], [], 0, false, 1, null, TimeSpan.Zero);
        _operations.RunQueryAsync(Secret, "SELECT 1", 200, null, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Run().HandleAsync(_connectionId, "SELECT 1", 200);

        result.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task Run_failures_are_friendly()
    {
        _operations.RunQueryAsync(Secret, "VACUUM", Arg.Any<int>(), null, Arg.Any<CancellationToken>())
            .Returns<Task<QueryResult>>(_ => throw new SqliteScriptRefusedException("VACUUM is not allowed (statement 1) — use Backups for a compacted copy."));

        var result = await Run().HandleAsync(_connectionId, "VACUUM");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("VACUUM is not allowed (statement 1) — use Backups for a compacted copy.");
    }

    [Fact]
    public async Task Run_on_an_unknown_connection_fails()
    {
        var result = await Run().HandleAsync(_unknownId, "SELECT 1");

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _operations.DidNotReceiveWithAnyArgs().RunQueryAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task A_cancelled_run_rethrows_instead_of_reporting_an_error()
    {
        using var cts = new CancellationTokenSource();
        _operations.RunQueryAsync(Secret, "SELECT 1", Arg.Any<int>(), null, Arg.Any<CancellationToken>()).Returns<Task<QueryResult>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Run().HandleAsync(_connectionId, "SELECT 1", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Explain_returns_the_plan()
    {
        var plan = new ExplainPlan("SELECT 1", 1, [new ExplainNode(2, 0, "SCAN events")]);
        _operations.ExplainQueryAsync(Secret, "SELECT 1", null, Arg.Any<CancellationToken>()).Returns(plan);

        var result = await Explain().HandleAsync(_connectionId, "SELECT 1");

        result.Value.Should().BeSameAs(plan);
    }

    [Fact]
    public async Task Run_and_explain_pass_a_backup_file_through()
    {
        var page = new QueryResult([], [], 0, false, 1, null, TimeSpan.Zero);
        var plan = new ExplainPlan("SELECT 1", 1, []);
        _operations.RunQueryAsync(Secret, "SELECT 1", 100, "reports-20261013-020000.db", Arg.Any<CancellationToken>()).Returns(page);
        _operations.ExplainQueryAsync(Secret, "SELECT 1", "reports-20261013-020000.db", Arg.Any<CancellationToken>()).Returns(plan);

        (await Run().HandleAsync(_connectionId, "SELECT 1", 100, "reports-20261013-020000.db")).Value.Should().BeSameAs(page);
        (await Explain().HandleAsync(_connectionId, "SELECT 1", "reports-20261013-020000.db")).Value.Should().BeSameAs(plan);
    }

    [Fact]
    public async Task Explain_failures_are_friendly()
    {
        _operations.ExplainQueryAsync(Secret, "SELECT 1", null, Arg.Any<CancellationToken>())
            .Returns<Task<ExplainPlan>>(_ => throw new Microsoft.Data.Sqlite.SqliteException("raw", 5));

        var result = await Explain().HandleAsync(_connectionId, "SELECT 1");

        result.Error.Should().StartWith("Database is locked");
    }

    [Fact]
    public async Task Dry_run_returns_the_rolled_back_result_without_auditing()
    {
        var dry = new WriteScriptResult(false, [new WriteStatementResult("DELETE", Sql, 3, 12)], TimeSpan.FromMilliseconds(40), null);
        _operations.DryRunWriteAsync(Secret, Sql, Arg.Any<CancellationToken>()).Returns(dry);

        var result = await DryRun().HandleAsync(_connectionId, Sql);

        result.Value.Should().BeSameAs(dry);
    }

    [Fact]
    public async Task Dry_run_on_a_read_only_connection_says_how_to_enable_writes()
    {
        _operations.DryRunWriteAsync(Secret, Sql, Arg.Any<CancellationToken>())
            .Returns<Task<WriteScriptResult>>(_ => throw new SqliteReadOnlyConnectionException());

        var result = await DryRun().HandleAsync(_connectionId, Sql);

        result.Error.Should().Be("This connection is read-only — set Default mode to Read-write in Connections.");
    }

    [Fact]
    public async Task A_cancelled_dry_run_rethrows()
    {
        using var cts = new CancellationTokenSource();
        _operations.DryRunWriteAsync(Secret, Sql, Arg.Any<CancellationToken>()).Returns<Task<WriteScriptResult>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => DryRun().HandleAsync(_connectionId, Sql, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
