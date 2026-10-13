using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

/// <summary>Design spec §11.5: create / rename / drop handlers and the drop impact query.</summary>
public class DdlCommandHandlerTests
{
    private const string Secret = "Path=/data/reports.db;Mode=ReadWrite;BackupDir=/data/backups";
    private const string Name = "sqlite-reports";
    private const string Ddl = "CREATE TABLE report_runs(id INTEGER PRIMARY KEY)";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _unknownId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 9, 40, 51, TimeSpan.Zero));

    public DdlCommandHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _connections.GetSecretAsync(_unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);
    }

    private static string Hash(string sql) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))[..12];

    private CreateTableCommandHandler Create() => new(_operations, _connections, _audit, NullLogger<CreateTableCommandHandler>.Instance);

    private RenameTableCommandHandler Rename() => new(_operations, _connections, _audit, NullLogger<RenameTableCommandHandler>.Instance);

    private GetDropImpactQueryHandler Impact() => new(_operations, _connections, NullLogger<GetDropImpactQueryHandler>.Instance);

    private DropTableCommandHandler Drop() => new(_operations, _connections, _audit,
        new RunBackupCommandHandler(_operations, _connections, _audit, _store, _time, NullLogger<RunBackupCommandHandler>.Instance),
        NullLogger<DropTableCommandHandler>.Instance);

    // ---- create ----

    [Fact]
    public async Task Create_returns_the_new_table_and_audits_mutating_with_hash_and_ddl()
    {
        _operations.CreateTableAsync(Secret, Ddl, Arg.Any<CancellationToken>()).Returns("report_runs");

        var result = await Create().HandleAsync(new CreateTableCommand(_connectionId, Name, Ddl));

        result.Value.Should().Be("report_runs");
        await _audit.Received(1).RecordAsync("sqlite.table.create", "sqlite-reports/report_runs", ActionRisk.Mutating, true,
            $"{Hash(Ddl)} · {Ddl}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_create_is_audited_as_failed_with_the_friendly_reason()
    {
        _operations.CreateTableAsync(Secret, "DROP TABLE x", Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new SqliteDdlRefusedException("Create table takes exactly one CREATE TABLE statement — use Query for anything else."));

        var result = await Create().HandleAsync(new CreateTableCommand(_connectionId, Name, "DROP TABLE x"));

        result.Error.Should().Be("Create table takes exactly one CREATE TABLE statement — use Query for anything else.");
        await _audit.Received(1).RecordAsync("sqlite.table.create", Name, ActionRisk.Mutating, false,
            $"{Hash("DROP TABLE x")} · {result.Error}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_runs_nothing_and_writes_no_audit_row()
    {
        (await Create().HandleAsync(new CreateTableCommand(_unknownId, Name, Ddl))).Error.Should().Be("Connection not found.");
        (await Rename().HandleAsync(new RenameTableCommand(_unknownId, Name, "a", "b"))).Error.Should().Be("Connection not found.");
        (await Drop().HandleAsync(new DropTableCommand(_unknownId, Name, "a", true, "nima"))).Error.Should().Be("Connection not found.");

        _audit.ReceivedCalls().Should().BeEmpty();
        _operations.ReceivedCalls().Should().BeEmpty();
    }

    // ---- rename ----

    [Fact]
    public async Task Rename_trims_the_new_name_and_audits_old_to_new()
    {
        var result = await Rename().HandleAsync(new RenameTableCommand(_connectionId, Name, "events", "  events_archive "));

        result.IsSuccess.Should().BeTrue();
        await _operations.Received(1).RenameTableAsync(Secret, "events", "events_archive", Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("sqlite.table.rename", "sqlite-reports/events", ActionRisk.Mutating, true,
            "→ events_archive", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_rename_is_audited_as_failed()
    {
        _operations.RenameTableAsync(Secret, "events", "sessions", Arg.Any<CancellationToken>())
            .Returns(_ => throw new SqliteDdlRefusedException("'sessions' is already taken by another table, view, index or trigger."));

        var result = await Rename().HandleAsync(new RenameTableCommand(_connectionId, Name, "events", "sessions"));

        result.Error.Should().Be("'sessions' is already taken by another table, view, index or trigger.");
        await _audit.Received(1).RecordAsync("sqlite.table.rename", "sqlite-reports/events", ActionRisk.Mutating, false,
            $"→ sessions · {result.Error}", Arg.Any<CancellationToken>());
    }

    // ---- drop impact ----

    [Fact]
    public async Task Drop_impact_is_a_plain_query()
    {
        var impact = new DropImpact("events", 41_200_000, 3, [new DropReference("event_props", ["event_id"], "CASCADE")]);
        _operations.GetDropImpactAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(impact);

        var result = await Impact().HandleAsync(_connectionId, "events");

        result.Value.Should().BeSameAs(impact);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    // ---- drop ----

    [Fact]
    public async Task Drop_without_backup_drops_and_audits_destructive()
    {
        var result = await Drop().HandleAsync(new DropTableCommand(_connectionId, Name, "events", false, "nima"));

        result.IsSuccess.Should().BeTrue();
        await _operations.Received(1).DropTableAsync(Secret, "events", Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().CreateBackupAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("sqlite.table.drop", "sqlite-reports/events", ActionRisk.Destructive, true,
            "no backup first", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Drop_with_backup_first_backs_up_then_drops_and_names_the_backup()
    {
        _operations.CreateBackupAsync(Secret, _time.GetUtcNow(), Arg.Any<CancellationToken>())
            .Returns(new BackupCreated("reports-20261013-094051.db", 4096, TimeSpan.FromSeconds(2)));
        _operations.VerifyBackupAsync(Secret, "reports-20261013-094051.db", Arg.Any<CancellationToken>())
            .Returns(new BackupVerification(true, ["ok"], new string('a', 64), new SqliteFileFingerprint(4096, 3, null, new string('f', 64), [])));

        var result = await Drop().HandleAsync(new DropTableCommand(_connectionId, Name, "events", true, "nima"));

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _operations.CreateBackupAsync(Secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            _operations.DropTableAsync(Secret, "events", Arg.Any<CancellationToken>());
        });
        var record = (await BackupHistoryStore.ReadAsync(_store, _connectionId)).Should().ContainSingle().Subject;
        record.Actor.Should().Be("nima");
        record.Note.Should().Be("before dropping events");
        await _audit.Received(1).RecordAsync("sqlite.table.drop", "sqlite-reports/events", ActionRisk.Destructive, true,
            "after backup reports-20261013-094051.db", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_backup_aborts_the_drop_with_a_failed_audit_row()
    {
        _operations.CreateBackupAsync(Secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<BackupCreated>>(_ => throw new SqliteException("disk full", 13));

        var result = await Drop().HandleAsync(new DropTableCommand(_connectionId, Name, "events", true, "nima"));

        result.Error.Should().Be("The backup failed, so nothing was dropped: The disk or volume is full.");
        await _operations.DidNotReceive().DropTableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("sqlite.table.drop", "sqlite-reports/events", ActionRisk.Destructive, false,
            "backup failed — nothing dropped · The disk or volume is full.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_drop_is_audited_as_failed()
    {
        _operations.DropTableAsync(Secret, "tenants", Arg.Any<CancellationToken>())
            .Returns(_ => throw new SqliteException("FOREIGN KEY constraint failed", 19));

        var result = await Drop().HandleAsync(new DropTableCommand(_connectionId, Name, "tenants", false, "nima"));

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("sqlite.table.drop", "sqlite-reports/tenants", ActionRisk.Destructive, false,
            result.Error, Arg.Any<CancellationToken>());
    }
}
