using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Browse;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Browse;

public class IndexCommandHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public IndexCommandHandlerTests() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private static IndexCommand Cmd(string? db = "app") => new(Id, "pg-prod-eu", db, 16500);
    private ReindexIndexCommandHandler Reindex() => new(_ops, _connections, _audit, NullLogger<ReindexIndexCommandHandler>.Instance);
    private DropIndexCommandHandler Drop() => new(_ops, _connections, _audit, NullLogger<DropIndexCommandHandler>.Instance);

    [Fact]
    public async Task Missing_connection_fails_without_audit()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Drop().HandleAsync(Cmd());

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Reindex_audits_mutating_against_the_qualified_name()
    {
        _ops.ReindexIndexAsync("secret", "app", 16500, Arg.Any<CancellationToken>()).Returns("\"public\".\"idx_orders_status\"");

        var result = await Reindex().HandleAsync(Cmd());

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("pg.index.reindex", "pg-prod-eu/app/\"public\".\"idx_orders_status\"",
            ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drop_audits_destructive_and_uses_default_for_a_null_database()
    {
        _ops.DropIndexAsync("secret", null, 16500, Arg.Any<CancellationToken>()).Returns("\"public\".\"i\"");

        var result = await Drop().HandleAsync(Cmd(null));

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("pg.index.drop", "pg-prod-eu/(default)/\"public\".\"i\"",
            ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refusal_fails_with_its_message_and_no_audit()
    {
        _ops.DropIndexAsync("secret", "app", 16500, Arg.Any<CancellationToken>())
            .ThrowsAsync(new PgSqlRefusedException("This index backs a constraint — drop the constraint instead."));

        var result = await Drop().HandleAsync(Cmd());

        result.Error.Should().Be("This index backs a constraint — drop the constraint instead.");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_failure_is_audited_against_the_oid_target()
    {
        _ops.ReindexIndexAsync("secret", "app", 16500, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Reindex().HandleAsync(Cmd());

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("pg.index.reindex", "pg-prod-eu/app/oid 16500", ActionRisk.Mutating, false,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
