using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Query;

public class QueryHandlersTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public QueryHandlersTests() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private ExplainQueryQueryHandler Explain() => new(_ops, _connections, NullLogger<ExplainQueryQueryHandler>.Instance);
    private ExportQueryCsvCommandHandler Export() => new(_ops, _connections, _audit, NullLogger<ExportQueryCsvCommandHandler>.Instance);

    [Fact]
    public async Task Explain_returns_the_plan_text()
    {
        _ops.ExplainAsync("secret", "app", "select 1", 30, Arg.Any<CancellationToken>()).Returns("Result");
        (await Explain().HandleAsync(Id, "app", "select 1", 30)).Value.Should().Be("Result");
    }

    [Fact]
    public async Task Explain_missing_connection_fails()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);
        (await Explain().HandleAsync(Id, "app", "select 1", 30)).Error.Should().Be(HandlerRunner.ConnectionNotFound);
    }

    [Fact]
    public async Task Explain_refusal_fails_with_the_friendly_message()
    {
        _ops.ExplainAsync("secret", "app", "drop table x", 30, Arg.Any<CancellationToken>()).ThrowsAsync(new PgSqlRefusedException("Nope."));
        (await Explain().HandleAsync(Id, "app", "drop table x", 30)).Error.Should().Be("Nope.");
    }

    [Fact]
    public async Task History_lists_newest_first_from_the_store()
    {
        var store = new InMemoryPluginStore();
        var at = DateTimeOffset.UtcNow;
        await QueryHistoryStore.AppendAsync(store, Id, new("a", false, 1, 0, null, "n", at, "h", null), at, default);
        await QueryHistoryStore.AppendAsync(store, Id, new("b", false, 1, 0, null, "n", at, "h", null), at, default);

        (await new ListQueryHistoryQueryHandler(store).HandleAsync(Id)).Select(e => e.Sql).Should().Equal("b", "a");
    }

    [Fact]
    public async Task Export_missing_connection_fails_without_audit()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);
        var result = await Export().HandleAsync(new(Id, "pg", "app", "select 1", 30));

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Export_success_audits_hash_and_summary()
    {
        _ops.ExportQueryCsvAsync("secret", "app", "select 1", 30, Arg.Any<CancellationToken>()).Returns(new CsvExport("q.csv", [1], 12, false));

        var result = await Export().HandleAsync(new(Id, "pg", "app", "select 1", 30));

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("pg.query.export", "pg/app", ActionRisk.Safe, true,
            $"{QueryAudit.Hash("select 1")} · 12 rows", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Export_failure_is_audited_failed_with_friendly_text()
    {
        _ops.ExportQueryCsvAsync("secret", null, "select 1", 30, Arg.Any<CancellationToken>()).ThrowsAsync(new PgSqlRefusedException("Nope."));

        var result = await Export().HandleAsync(new(Id, "pg", null, "select 1", 30));

        result.Error.Should().Be("Nope.");
        await _audit.Received(1).RecordAsync("pg.query.export", "pg/(default)", ActionRisk.Safe, false,
            $"{QueryAudit.Hash("select 1")} · Nope.", Arg.Any<CancellationToken>());
    }
}
