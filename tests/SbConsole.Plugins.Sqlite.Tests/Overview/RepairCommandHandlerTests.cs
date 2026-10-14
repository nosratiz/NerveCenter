using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Overview;

/// <summary>Design spec §11.6: REINDEX and Recover into new file.</summary>
public class RepairCommandHandlerTests
{
    private const string Secret = "Path=/data/reports.db;Mode=ReadWrite;BackupDir=/data/backups";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 40, 51, TimeSpan.Zero);
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);

    public RepairCommandHandlerTests() =>
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);

    private ReindexCommandHandler Reindex() => new(_operations, _connections, _audit, _store, _time, NullLogger<ReindexCommandHandler>.Instance);

    private RecoverCommandHandler Recover() => new(_operations, _connections, _audit, _time, NullLogger<RecoverCommandHandler>.Instance);

    [Fact]
    public async Task Reindex_stores_the_fresh_integrity_result_and_audits_mutating()
    {
        _operations.ReindexAsync(Secret, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _time.Advance(TimeSpan.FromSeconds(3));
            return new IntegrityCheckOutcome(["ok"], 0);
        });

        var result = await Reindex().HandleAsync(new ReindexCommand(_connectionId, "sqlite-reports"));

        result.Value.Should().BeEquivalentTo(new IntegrityResult(true, ["ok"], 0, Now, TimeSpan.FromSeconds(3)));
        (await IntegrityResultStore.ReadAsync(_store, _connectionId)).Should().BeEquivalentTo(result.Value);
        await _audit.Received(1).RecordAsync("sqlite.reindex", "sqlite-reports", ActionRisk.Mutating, true,
            "integrity ok after REINDEX · 0 FK violations", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reindex_that_leaves_problems_still_succeeds_and_says_so()
    {
        _operations.ReindexAsync(Secret, Arg.Any<CancellationToken>())
            .Returns(new IntegrityCheckOutcome(["*** in database main ***", "Page 48112: btreeInitPage() returns error code 11"], 1));

        var result = await Reindex().HandleAsync(new ReindexCommand(_connectionId, "sqlite-reports"));

        result.Value!.IntegrityOk.Should().BeFalse();
        await _audit.Received(1).RecordAsync("sqlite.reindex", "sqlite-reports", ActionRisk.Mutating, true,
            "2 problems remain after REINDEX · 1 FK violations", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_reindex_keeps_the_previous_result_and_is_audited_as_failed()
    {
        var previous = new IntegrityResult(false, ["row 2041 missing from index idx_events_ts"], 0, Now.AddHours(-1), TimeSpan.FromSeconds(4));
        await IntegrityResultStore.WriteAsync(_store, _connectionId, previous);
        _operations.ReindexAsync(Secret, Arg.Any<CancellationToken>()).Returns<Task<IntegrityCheckOutcome>>(_ => throw new SqliteReadOnlyConnectionException());

        var result = await Reindex().HandleAsync(new ReindexCommand(_connectionId, "sqlite-reports"));

        result.Error.Should().Be("This connection is read-only — set Default mode to Read-write in Connections.");
        (await IntegrityResultStore.ReadAsync(_store, _connectionId)).Should().BeEquivalentTo(previous);
        await _audit.Received(1).RecordAsync("sqlite.reindex", "sqlite-reports", ActionRisk.Mutating, false, result.Error, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recover_passes_the_clock_and_audits_the_outcome()
    {
        var outcome = new SalvageOutcome("reports-recovered-20261013-094051.db", 7, 1_204_117, 3, 9,
            [new SalvageFailure("table", "events", "stopped after 41,000 rows — The file is not a valid or healthy SQLite database.")], TimeSpan.FromSeconds(12));
        _operations.SalvageAsync(Secret, Now, Arg.Any<CancellationToken>()).Returns(outcome);

        var result = await Recover().HandleAsync(new RecoverCommand(_connectionId, "sqlite-reports"));

        result.Value.Should().BeSameAs(outcome);
        await _audit.Received(1).RecordAsync("sqlite.recover", "sqlite-reports", ActionRisk.Mutating, true,
            "reports-recovered-20261013-094051.db · 7 tables · 1,204,117 rows (3 skipped) · 1 failed", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_recover_is_audited_as_failed()
    {
        _operations.SalvageAsync(Secret, Now, Arg.Any<CancellationToken>())
            .Returns<Task<SalvageOutcome>>(_ => throw new SqliteException("disk full", 13));

        var result = await Recover().HandleAsync(new RecoverCommand(_connectionId, "sqlite-reports"));

        result.Error.Should().Be("The disk or volume is full.");
        await _audit.Received(1).RecordAsync("sqlite.recover", "sqlite-reports", ActionRisk.Mutating, false, "The disk or volume is full.", Arg.Any<CancellationToken>());
    }
}
