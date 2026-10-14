using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Activity;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Activity;

public class BackendCommandHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public BackendCommandHandlerTests() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private static readonly BackendCommand Cmd = new(Id, "pg-prod-eu", 48213);

    private static PgBackend Backend(string app = "billing-api", string query = "UPDATE\n   accounts SET x = 1") =>
        new(48213, "app_user", app, "10.0.0.1", "idle in transaction", null, query, null, null, null, [], null, "app");

    private CancelBackendCommandHandler Cancel() => new(_ops, _connections, _audit, NullLogger<CancelBackendCommandHandler>.Instance);
    private TerminateBackendCommandHandler Terminate() => new(_ops, _connections, _audit, NullLogger<TerminateBackendCommandHandler>.Instance);

    private async Task NoAudit() =>
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);

    [Fact]
    public async Task Missing_connection_fails_without_audit()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Cancel().HandleAsync(Cmd);

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await NoAudit();
    }

    [Fact]
    public async Task Cancel_success_audits_mutating_with_detail()
    {
        _ops.GetBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(Backend());
        _ops.CancelBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Cancel().HandleAsync(Cmd);

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("pg.backend.cancel", "pg-prod-eu/pid 48213", ActionRisk.Mutating, true,
            "pid 48213 · app_user · billing-api · UPDATE accounts SET x = 1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Terminate_success_audits_destructive()
    {
        _ops.GetBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(Backend());
        _ops.TerminateBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Terminate().HandleAsync(Cmd);

        result.IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("pg.backend.terminate", "pg-prod-eu/pid 48213", ActionRisk.Destructive, true,
            Arg.Is<string?>(d => d!.StartsWith("pid 48213 · app_user")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pid_missing_from_the_list_is_audited_as_ended_and_never_signalled()
    {
        _ops.GetBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns((PgBackend?)null);

        var result = await Terminate().HandleAsync(Cmd);

        result.Error.Should().Be("pid 48213 had already ended.");
        await _ops.DidNotReceiveWithAnyArgs().TerminateBackendAsync(default!, default, default);
        await _audit.Received(1).RecordAsync("pg.backend.terminate", "pg-prod-eu/pid 48213", ActionRisk.Destructive, false,
            "pid 48213 had already ended.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_signal_that_returns_false_is_audited_as_ended()
    {
        _ops.GetBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(Backend());
        _ops.CancelBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Cancel().HandleAsync(Cmd);

        result.Error.Should().Be("pid 48213 had already ended.");
        await _audit.Received(1).RecordAsync("pg.backend.cancel", "pg-prod-eu/pid 48213", ActionRisk.Mutating, false,
            "pid 48213 had already ended.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_consoles_own_connections_are_refused_without_audit_or_signal()
    {
        _ops.GetBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(Backend("sbconsole"));

        var cancel = await Cancel().HandleAsync(Cmd);
        var terminate = await Terminate().HandleAsync(Cmd);

        cancel.Error.Should().Be("That's one of this console's own connections.");
        terminate.Error.Should().Be("That's one of this console's own connections.");
        await _ops.DidNotReceiveWithAnyArgs().CancelBackendAsync(default!, default, default);
        await _ops.DidNotReceiveWithAnyArgs().TerminateBackendAsync(default!, default, default);
        await NoAudit();
    }

    [Fact]
    public async Task A_read_only_refusal_fails_with_its_message_and_no_audit()
    {
        _ops.GetBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(Backend());
        _ops.CancelBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).ThrowsAsync(new PgSqlRefusedException("This connection is read-only."));

        var result = await Cancel().HandleAsync(Cmd);

        result.Error.Should().Be("This connection is read-only.");
        await NoAudit();
    }

    [Fact]
    public async Task An_unexpected_failure_is_friendly_and_audited_as_failed()
    {
        _ops.GetBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).Returns(Backend());
        _ops.TerminateBackendAsync("secret", 48213, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Terminate().HandleAsync(Cmd);

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("pg.backend.terminate", "pg-prod-eu/pid 48213", ActionRisk.Destructive, false,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_activity_query_returns_the_snapshot()
    {
        var activity = new PgActivity(1, DateTimeOffset.UnixEpoch, []);
        _ops.GetActivityAsync("secret", Arg.Any<CancellationToken>()).Returns(activity);

        var result = await new GetActivityQueryHandler(_ops, _connections, NullLogger<GetActivityQueryHandler>.Instance).HandleAsync(Id);

        result.Value.Should().Be(activity);
    }
}
