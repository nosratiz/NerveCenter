using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Overview;

public class IntegrityTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero));

    private RunIntegrityCheckCommandHandler Handler() =>
        new(_operations, _connections, _audit, _store, _time, NullLogger<RunIntegrityCheckCommandHandler>.Instance);

    private RunIntegrityCheckCommand Command => new(_connectionId, "sqlite-reports");

    [Fact]
    public async Task A_check_is_timed_stored_and_audited_as_a_safe_action()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _time.Advance(TimeSpan.FromSeconds(41));
            return new IntegrityCheckOutcome(["ok"], 0);
        });

        var result = await Handler().HandleAsync(Command);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new IntegrityResult(true, ["ok"], 0, new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(41)));
        (await IntegrityResultStore.ReadAsync(_store, _connectionId)).Should().BeEquivalentTo(result.Value);
        await _audit.Received(1).RecordAsync("sqlite.integrity.check", "sqlite-reports", ActionRisk.Safe, true,
            "ok · 0 FK violations", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_check_is_still_a_successful_command_that_reports_the_problems()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>())
            .Returns(new IntegrityCheckOutcome(["Page 48112: btreeInitPage() returns error code 11", "row 2041 missing from index idx_events_ts"], 0));

        var result = await Handler().HandleAsync(Command);

        result.Value!.IntegrityOk.Should().BeFalse();
        await _audit.Received(1).RecordAsync("sqlite.integrity.check", "sqlite-reports", ActionRisk.Safe, true,
            "2 problems · 0 FK violations", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_error_running_the_check_is_audited_as_failed_and_nothing_is_stored()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<IntegrityCheckOutcome>>(_ => throw new SqliteStatementTimeoutException(30, new TimeoutException()));

        var result = await Handler().HandleAsync(Command);

        result.Error.Should().Be("Statement exceeded the 30 s statement timeout.");
        _store.Values.Should().BeEmpty();
        await _audit.Received(1).RecordAsync("sqlite.integrity.check", "sqlite-reports", ActionRisk.Safe, false,
            "Statement exceeded the 30 s statement timeout.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_fails_without_an_audit_row()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(Command);

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task The_last_result_query_reads_what_the_command_stored()
    {
        var stored = new IntegrityResult(true, ["ok"], 0, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(3));
        await IntegrityResultStore.WriteAsync(_store, _connectionId, stored);

        var result = await new GetLastIntegrityResultQueryHandler(_store).HandleAsync(_connectionId);

        result.Should().BeEquivalentTo(stored);
    }

    [Fact]
    public async Task A_corrupt_stored_value_reads_as_never_run()
    {
        _store.Values[IntegrityResultStore.Key(_connectionId)] = "{not json";

        (await IntegrityResultStore.ReadAsync(_store, _connectionId)).Should().BeNull();
    }
}
