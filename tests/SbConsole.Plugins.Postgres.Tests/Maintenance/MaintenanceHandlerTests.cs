using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Maintenance;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Maintenance;

public class MaintenanceHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public MaintenanceHandlerTests() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private RunMaintenanceCommandHandler Run() => new(_ops, _connections, _audit, NullLogger<RunMaintenanceCommandHandler>.Instance);

    private static MaintenanceCommand Cmd(PgMaintenanceKind kind, string? db = "app") => new(Id, "pg-prod-eu", db, 16500, kind);

    [Theory]
    [InlineData(PgMaintenanceKind.Vacuum, "pg.maintenance.vacuum", ActionRisk.Mutating)]
    [InlineData(PgMaintenanceKind.Analyze, "pg.maintenance.analyze", ActionRisk.Mutating)]
    [InlineData(PgMaintenanceKind.VacuumFull, "pg.maintenance.vacuum_full", ActionRisk.Destructive)]
    public async Task Each_kind_audits_its_action_and_risk_against_the_qualified_name(PgMaintenanceKind kind, string action, ActionRisk risk)
    {
        _ops.RunMaintenanceAsync("secret", "app", 16500, kind, Arg.Any<CancellationToken>()).Returns("\"public\".\"orders\"");

        var result = await Run().HandleAsync(Cmd(kind));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("\"public\".\"orders\"");
        await _audit.Received(1).RecordAsync(action, "pg-prod-eu/app/\"public\".\"orders\"", risk, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_null_database_is_audited_as_default()
    {
        _ops.RunMaintenanceAsync("secret", null, 16500, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>()).Returns("\"public\".\"t\"");

        await Run().HandleAsync(Cmd(PgMaintenanceKind.Vacuum, null));

        await _audit.Received(1).RecordAsync("pg.maintenance.vacuum", "pg-prod-eu/(default)/\"public\".\"t\"",
            ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failure_is_audited_against_the_oid()
    {
        _ops.RunMaintenanceAsync("secret", "app", 16500, PgMaintenanceKind.VacuumFull, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Run().HandleAsync(Cmd(PgMaintenanceKind.VacuumFull));

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("pg.maintenance.vacuum_full", "pg-prod-eu/app/oid 16500",
            ActionRisk.Destructive, false, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refusal_fails_with_its_message_and_no_audit()
    {
        _ops.RunMaintenanceAsync("secret", "app", 16500, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>())
            .ThrowsAsync(new PgSqlRefusedException("That table no longer exists."));

        var result = await Run().HandleAsync(Cmd(PgMaintenanceKind.Vacuum));

        result.Error.Should().Be("That table no longer exists.");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_missing_connection_fails_without_audit()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Run().HandleAsync(Cmd(PgMaintenanceKind.Vacuum));

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task The_query_handler_returns_the_maintenance_read()
    {
        var data = new PgMaintenance(true, 3, [], 1, 2, false, []);
        _ops.GetMaintenanceAsync("secret", "app", Arg.Any<CancellationToken>()).Returns(data);

        var result = await new GetMaintenanceQueryHandler(_ops, _connections, NullLogger<GetMaintenanceQueryHandler>.Instance)
            .HandleAsync(Id, "app");

        result.Value.Should().BeSameAs(data);
    }
}
