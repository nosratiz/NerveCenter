using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;
using static SbConsole.Plugins.Sqlite.Tests.Backups.BackupTestData;

namespace SbConsole.Plugins.Sqlite.Tests.Backups;

public class BackupCommandHandlerTests
{
    private const string Secret = "Path=/data/reports.db;Mode=ReadWrite;BackupDir=/data/backups";
    private const string Target = "sqlite-reports";
    private const string FileName = "reports-20261013-020000.db";
    private static readonly DateTimeOffset Now = Utc(2026, 10, 13, 2);

    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _unknownId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);

    public BackupCommandHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _connections.GetSecretAsync(_unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);
    }

    private static BackupVerification Verified(bool ok = true) => new(
        ok, ok ? ["ok"] : ["row 2041 missing from index idx_events_ts", "Page 48112: btreeInitPage() returns error code 11"],
        new string('a', 64), new SqliteFileFingerprint(1000, 4, 120, new string('f', 64), []));

    private RunBackupCommandHandler RunBackup() => new(_operations, _connections, _audit, _store, _time, NullLogger<RunBackupCommandHandler>.Instance);

    private VerifyBackupCommandHandler Verify() => new(_operations, _connections, _audit, _store, _time, NullLogger<VerifyBackupCommandHandler>.Instance);

    private PruneBackupsCommandHandler Prune() => new(_operations, _connections, _audit, _store, _time, NullLogger<PruneBackupsCommandHandler>.Instance);

    private DownloadBackupCommandHandler Download() => new(_operations, _connections, _audit, _store, NullLogger<DownloadBackupCommandHandler>.Instance);

    private RestoreBackupCommandHandler Restore() => new(_operations, _connections, _audit, _store, _time, NullLogger<RestoreBackupCommandHandler>.Instance);

    private SaveBackupPolicyCommandHandler SavePolicy() => new(_connections, _audit, _store, _time, NullLogger<SaveBackupPolicyCommandHandler>.Instance);

    private Task<IReadOnlyList<BackupRecord>> History() => BackupHistoryStore.ReadAsync(_store, _connectionId);

    private async Task<BackupRecord> Stored(BackupRecord record)
    {
        await BackupHistoryStore.AddAsync(_store, _connectionId, record);
        return record;
    }

    // ---- Back up now ----

    [Fact]
    public async Task A_backup_is_recorded_verified_and_audited_as_safe()
    {
        _operations.CreateBackupAsync(Secret, Now, Arg.Any<CancellationToken>()).Returns(new BackupCreated(FileName, 1_048_576, TimeSpan.FromSeconds(3)));
        _operations.VerifyBackupAsync(Secret, FileName, Arg.Any<CancellationToken>()).Returns(Verified());

        var result = await RunBackup().HandleAsync(new RunBackupCommand(_connectionId, Target, "nima", Note: "before migration"));

        result.IsSuccess.Should().BeTrue();
        var record = (await History()).Should().ContainSingle().Subject;
        record.Should().BeEquivalentTo(result.Value);
        record.Should().BeEquivalentTo(new
        {
            FileName,
            TakenAt = Now,
            Bytes = 1_048_576L,
            DurationMs = 3000L,
            Trigger = "manual",
            Actor = "nima",
            Note = "before migration",
            Status = "ok",
            Error = (string?)null,
            Verify = "ok",
            VerifyDetail = (string?)null,
            Sha256 = new string('a', 64),
            Tables = 4L,
            RowsApprox = 120L,
            SchemaFingerprint = new string('f', 64),
            VerifiedAt = Now,
        });
        await _audit.Received(1).RecordAsync("sqlite.backup.create", Target, ActionRisk.Safe, true,
            $"{FileName} · 1 MB · 3 s · manual · before migration", Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().RecordAsync("sqlite.backup.verify", Arg.Any<string>(), Arg.Any<ActionRisk>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_backup_is_still_recorded_with_its_friendly_error()
    {
        _operations.CreateBackupAsync(Secret, Now, Arg.Any<CancellationToken>()).Returns<Task<BackupCreated>>(_ =>
        {
            _time.Advance(TimeSpan.FromSeconds(2));
            throw new SqliteException("disk I/O: full", 13);
        });

        var result = await RunBackup().HandleAsync(new RunBackupCommand(_connectionId, Target, "system", BackupRecord.TriggerSchedule));

        result.Error.Should().Be("The disk or volume is full.");
        (await History()).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            FileName = (string?)null,
            TakenAt = Now,
            DurationMs = 2000L,
            Trigger = "schedule",
            Actor = "system",
            Status = "failed",
            Error = "The disk or volume is full.",
            Verify = "unverified",
        });
        await _audit.Received(1).RecordAsync("sqlite.backup.create", Target, ActionRisk.Safe, false,
            "schedule · The disk or volume is full.", Arg.Any<CancellationToken>());
        await _operations.DidNotReceiveWithAnyArgs().VerifyBackupAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_backup_that_fails_its_verify_is_kept_and_marked()
    {
        _operations.CreateBackupAsync(Secret, Now, Arg.Any<CancellationToken>()).Returns(new BackupCreated(FileName, 10, TimeSpan.Zero));
        _operations.VerifyBackupAsync(Secret, FileName, Arg.Any<CancellationToken>()).Returns(Verified(ok: false));

        var result = await RunBackup().HandleAsync(new RunBackupCommand(_connectionId, Target, "nima"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Verify.Should().Be("failed");
        result.Value.VerifyDetail.Should().Be("row 2041 missing from index idx_events_ts (+1 more)");
        (await History()).Single().Verify.Should().Be("failed");
    }

    [Fact]
    public async Task A_verify_that_cannot_read_the_copy_marks_it_failed_with_the_reason()
    {
        _operations.CreateBackupAsync(Secret, Now, Arg.Any<CancellationToken>()).Returns(new BackupCreated(FileName, 10, TimeSpan.Zero));
        _operations.VerifyBackupAsync(Secret, FileName, Arg.Any<CancellationToken>())
            .Returns<Task<BackupVerification>>(_ => throw new SqliteException("raw", 26));

        var result = await RunBackup().HandleAsync(new RunBackupCommand(_connectionId, Target, "nima"));

        result.IsSuccess.Should().BeTrue();
        (await History()).Single().Should().BeEquivalentTo(new { Status = "ok", Verify = "failed", VerifyDetail = "The file is not a valid or healthy SQLite database." });
    }

    [Fact]
    public async Task Cancelling_during_the_verify_keeps_the_backup_unverified()
    {
        using var cts = new CancellationTokenSource();
        _operations.CreateBackupAsync(Secret, Now, Arg.Any<CancellationToken>()).Returns(new BackupCreated(FileName, 10, TimeSpan.Zero));
        _operations.VerifyBackupAsync(Secret, FileName, Arg.Any<CancellationToken>()).Returns<Task<BackupVerification>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var result = await RunBackup().HandleAsync(new RunBackupCommand(_connectionId, Target, "nima"), cts.Token);

        result.IsSuccess.Should().BeTrue();
        (await History()).Single().Verify.Should().Be("unverified");
    }

    [Fact]
    public async Task A_cancelled_backup_rethrows_and_records_nothing()
    {
        using var cts = new CancellationTokenSource();
        _operations.CreateBackupAsync(Secret, Now, Arg.Any<CancellationToken>()).Returns<Task<BackupCreated>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => RunBackup().HandleAsync(new RunBackupCommand(_connectionId, Target, "nima"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await History()).Should().BeEmpty();
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_backup_of_an_unknown_connection_fails_without_history_or_audit()
    {
        var result = await RunBackup().HandleAsync(new RunBackupCommand(_unknownId, Target, "nima"));

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        _store.Values.Should().BeEmpty();
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    // ---- Verify ----

    [Fact]
    public async Task Verify_updates_the_record_and_audits_the_hash()
    {
        var record = await Stored(Ok(Now.AddDays(-1), fileName: FileName));
        _time.Advance(TimeSpan.FromHours(1));
        _operations.VerifyBackupAsync(Secret, FileName, Arg.Any<CancellationToken>()).Returns(Verified());

        var result = await Verify().HandleAsync(new VerifyBackupCommand(_connectionId, Target, record.Id));

        result.Value!.Verification.Should().BeEquivalentTo(Verified());
        result.Value.Record.Should().BeEquivalentTo(new { Verify = "ok", VerifiedAt = Now.AddHours(1), Tables = 4L });
        (await History()).Single().Should().BeEquivalentTo(result.Value.Record);
        await _audit.Received(1).RecordAsync("sqlite.backup.verify", Target, ActionRisk.Safe, true,
            $"{FileName} · ok · sha256:aaaaaaaaaaaa", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_reports_integrity_problems_as_a_successful_check()
    {
        var record = await Stored(Ok(Now, fileName: FileName));
        _operations.VerifyBackupAsync(Secret, FileName, Arg.Any<CancellationToken>()).Returns(Verified(ok: false));

        var result = await Verify().HandleAsync(new VerifyBackupCommand(_connectionId, Target, record.Id));

        result.Value!.Record.Verify.Should().Be("failed");
        await _audit.Received(1).RecordAsync("sqlite.backup.verify", Target, ActionRisk.Safe, true,
            $"{FileName} · failed · 2 problems", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_of_a_missing_file_fails_and_records_why()
    {
        var record = await Stored(Ok(Now, fileName: FileName));
        _operations.VerifyBackupAsync(Secret, FileName, Arg.Any<CancellationToken>())
            .Returns<Task<BackupVerification>>(_ => throw new SqliteBackupException($"Backup file '{FileName}' was not found in the backup directory."));

        var result = await Verify().HandleAsync(new VerifyBackupCommand(_connectionId, Target, record.Id));

        result.Error.Should().Be($"Backup file '{FileName}' was not found in the backup directory.");
        (await History()).Single().Should().BeEquivalentTo(new { Verify = "failed", VerifyDetail = result.Error });
        await _audit.Received(1).RecordAsync("sqlite.backup.verify", Target, ActionRisk.Safe, false,
            $"{FileName} · {result.Error}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_of_an_unknown_or_fileless_record_fails_without_audit()
    {
        var failed = await Stored(Failed(Now));

        (await Verify().HandleAsync(new VerifyBackupCommand(_connectionId, Target, Guid.NewGuid()))).Error.Should().Be(BackupHandlerSupport.BackupNotFound);
        (await Verify().HandleAsync(new VerifyBackupCommand(_connectionId, Target, failed.Id))).Error.Should().Be(BackupHandlerSupport.NoFile);
        (await Verify().HandleAsync(new VerifyBackupCommand(_unknownId, Target, failed.Id))).Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_cancelled_verify_rethrows()
    {
        using var cts = new CancellationTokenSource();
        var record = await Stored(Ok(Now, fileName: FileName));
        _operations.VerifyBackupAsync(Secret, FileName, Arg.Any<CancellationToken>()).Returns<Task<BackupVerification>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Verify().HandleAsync(new VerifyBackupCommand(_connectionId, Target, record.Id), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Prune ----

    [Fact]
    public async Task Prune_deletes_only_what_retention_picks_and_only_history_listed_files()
    {
        // Policy keeps 1 day, no weeks/months: of three successful backups only the newest stays.
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true, KeepDaily: 1, KeepWeekly: 0, KeepMonthly: 0));
        var newest = await Stored(Ok(Now, fileName: "reports-20261013-020000.db"));
        await Stored(Ok(Now.AddDays(-1), fileName: "reports-20261012-020000.db") with { Bytes = 300 });
        await Stored(Ok(Now.AddDays(-2), fileName: "reports-20261011-020000.db") with { Bytes = 200 });
        await Stored(Failed(Now.AddDays(-3)));

        var result = await Prune().HandleAsync(new PruneBackupsCommand(_connectionId, Target));

        result.Value.Should().Be(new PruneResult(3, 500, 0));
        (await History()).Should().ContainSingle().Which.Id.Should().Be(newest.Id);
        await _operations.Received(1).DeleteBackupFileAsync(Secret, "reports-20261012-020000.db", Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteBackupFileAsync(Secret, "reports-20261011-020000.db", Arg.Any<CancellationToken>());
        _operations.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(ISqliteOperations.DeleteBackupFileAsync)).Should().HaveCount(2);
        await _audit.Received(1).RecordAsync("sqlite.backup.prune", Target, ActionRisk.Destructive, true,
            "3 removed · 500 B freed · daily 02:00 UTC · 1d · 0w · 0m", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prune_without_a_saved_policy_uses_the_defaults()
    {
        await Stored(Ok(Now, fileName: "a.db"));
        await Stored(Ok(Now.AddDays(-2), fileName: "b.db"));

        var result = await Prune().HandleAsync(new PruneBackupsCommand(_connectionId, Target));

        result.Value.Should().Be(new PruneResult(0, 0, 0));
        await _operations.DidNotReceiveWithAnyArgs().DeleteBackupFileAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_keeps_its_record_and_fails_the_audit()
    {
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true, KeepDaily: 1, KeepWeekly: 0, KeepMonthly: 0));
        await Stored(Ok(Now, fileName: "keep.db"));
        await Stored(Ok(Now.AddDays(-1), fileName: "stuck.db"));
        await Stored(Ok(Now.AddDays(-2), fileName: "gone.db"));
        _operations.DeleteBackupFileAsync(Secret, "stuck.db", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new SqlitePathRefusedException("outside the roots"));

        var result = await Prune().HandleAsync(new PruneBackupsCommand(_connectionId, Target));

        result.Value.Should().Be(new PruneResult(1, 1000, 1));
        (await History()).Select(r => r.FileName).Should().Equal("keep.db", "stuck.db");
        await _audit.Received(1).RecordAsync("sqlite.backup.prune", Target, ActionRisk.Destructive, false,
            Arg.Is<string>(d => d.StartsWith("1 removed · 1000 B freed", StringComparison.Ordinal) && d.EndsWith("1 failed: outside the roots", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prune_of_an_unknown_connection_fails_without_audit()
    {
        (await Prune().HandleAsync(new PruneBackupsCommand(_unknownId, Target))).Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_cancelled_prune_still_records_what_it_deleted_then_rethrows()
    {
        using var cts = new CancellationTokenSource();
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true, KeepDaily: 1, KeepWeekly: 0, KeepMonthly: 0));
        await Stored(Ok(Now, fileName: "keep.db"));
        await Stored(Ok(Now.AddDays(-1), fileName: "first.db"));
        await Stored(Ok(Now.AddDays(-2), fileName: "second.db"));
        _operations.DeleteBackupFileAsync(Secret, "first.db", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        });

        var act = () => Prune().HandleAsync(new PruneBackupsCommand(_connectionId, Target), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await History()).Select(r => r.FileName).Should().Equal("keep.db", "second.db");
        await _audit.Received(1).RecordAsync("sqlite.backup.prune", Target, ActionRisk.Destructive, true,
            Arg.Is<string>(d => d.StartsWith("1 removed", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    // ---- Download ----

    [Fact]
    public async Task A_download_opens_the_stream_and_is_audited()
    {
        var record = await Stored(Ok(Now, fileName: FileName));
        var stream = new BackupFileStream(FileName, new MemoryStream([1, 2, 3]), 2048);
        _operations.OpenBackupStreamAsync(Secret, FileName, Arg.Any<CancellationToken>()).Returns(stream);

        var result = await Download().HandleAsync(new DownloadBackupCommand(_connectionId, Target, record.Id));

        result.Value.Should().BeSameAs(stream);
        await _audit.Received(1).RecordAsync("sqlite.backup.download", Target, ActionRisk.Safe, true, $"{FileName} · 2 KB", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_download_is_friendly_and_audited_as_failed()
    {
        var record = await Stored(Ok(Now, fileName: FileName));
        _operations.OpenBackupStreamAsync(Secret, FileName, Arg.Any<CancellationToken>())
            .Returns<Task<BackupFileStream>>(_ => throw new SqliteBackupException("Backup file 'x' was not found in the backup directory."));

        var result = await Download().HandleAsync(new DownloadBackupCommand(_connectionId, Target, record.Id));

        result.Error.Should().Be("Backup file 'x' was not found in the backup directory.");
        await _audit.Received(1).RecordAsync("sqlite.backup.download", Target, ActionRisk.Safe, false, $"{FileName} · {result.Error}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_download_of_an_unknown_backup_fails_without_audit()
    {
        (await Download().HandleAsync(new DownloadBackupCommand(_connectionId, Target, Guid.NewGuid()))).Error.Should().Be(BackupHandlerSupport.BackupNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    // ---- Restore ----

    [Fact]
    public async Task A_restore_is_audited_as_destructive_with_the_pre_restore_copy()
    {
        var record = await Stored(Ok(Now.AddDays(-1), fileName: FileName));
        _operations.RestoreBackupAsync(Secret, FileName, Now, Arg.Any<CancellationToken>())
            .Returns(new RestoreOutcome("reports.db.pre-restore-20261013-020000", true, ["ok"], TimeSpan.FromSeconds(12)));

        var result = await Restore().HandleAsync(new RestoreBackupCommand(_connectionId, Target, record.Id));

        result.Value!.PreRestoreFileName.Should().Be("reports.db.pre-restore-20261013-020000");
        await _audit.Received(1).RecordAsync("sqlite.backup.restore", Target, ActionRisk.Destructive, true,
            $"{FileName} · previous file kept as reports.db.pre-restore-20261013-020000 · integrity ok · 12 s", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_restore_refused_on_a_read_only_connection_is_friendly_and_audited_as_failed()
    {
        var record = await Stored(Ok(Now, fileName: FileName));
        _operations.RestoreBackupAsync(Secret, FileName, Now, Arg.Any<CancellationToken>())
            .Returns<Task<RestoreOutcome>>(_ => throw new SqliteReadOnlyConnectionException());

        var result = await Restore().HandleAsync(new RestoreBackupCommand(_connectionId, Target, record.Id));

        result.Error.Should().Be("This connection is read-only — set Default mode to Read-write in Connections.");
        await _audit.Received(1).RecordAsync("sqlite.backup.restore", Target, ActionRisk.Destructive, false,
            $"{FileName} · {result.Error}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_restore_of_an_unknown_or_fileless_backup_fails_without_audit()
    {
        var failed = await Stored(Failed(Now));

        (await Restore().HandleAsync(new RestoreBackupCommand(_connectionId, Target, Guid.NewGuid()))).Error.Should().Be(BackupHandlerSupport.BackupNotFound);
        (await Restore().HandleAsync(new RestoreBackupCommand(_connectionId, Target, failed.Id))).Error.Should().Be(BackupHandlerSupport.NoFile);
        await _operations.DidNotReceiveWithAnyArgs().RestoreBackupAsync(default!, default!, default, default);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_cancelled_restore_rethrows()
    {
        using var cts = new CancellationTokenSource();
        var record = await Stored(Ok(Now, fileName: FileName));
        _operations.RestoreBackupAsync(Secret, FileName, Now, Arg.Any<CancellationToken>()).Returns<Task<RestoreOutcome>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Restore().HandleAsync(new RestoreBackupCommand(_connectionId, Target, record.Id), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Policy ----

    [Fact]
    public async Task Saving_a_policy_stores_it_and_audits_it_as_mutating()
    {
        var policy = new BackupPolicy(Enabled: true, DailyAtUtc: "03:15", KeepDaily: 5);

        var result = await SavePolicy().HandleAsync(new SaveBackupPolicyCommand(_connectionId, Target, policy));

        result.IsSuccess.Should().BeTrue();
        (await BackupPolicyStore.ReadAsync(_store, _connectionId)).Should().Be(policy with { EnabledSince = Now });
        await _audit.Received(1).RecordAsync("sqlite.backup.policy", Target, ActionRisk.Mutating, true,
            "daily 03:15 UTC · 5d · 4w · 3m", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabling_a_disabled_policy_stamps_enabled_since_with_now()
    {
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: false, EnabledSince: Utc(2026, 1, 1)));

        await SavePolicy().HandleAsync(new SaveBackupPolicyCommand(_connectionId, Target, new BackupPolicy(Enabled: true, EnabledSince: Utc(2020, 1, 1))));

        (await BackupPolicyStore.ReadAsync(_store, _connectionId))!.EnabledSince.Should().Be(Now);
    }

    [Fact]
    public async Task Saving_an_already_enabled_policy_keeps_enabled_since()
    {
        var since = Utc(2026, 10, 1, 9);
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true, EnabledSince: since));

        await SavePolicy().HandleAsync(new SaveBackupPolicyCommand(_connectionId, Target, new BackupPolicy(Enabled: true, DailyAtUtc: "04:00")));

        (await BackupPolicyStore.ReadAsync(_store, _connectionId)).Should().Be(new BackupPolicy(Enabled: true, DailyAtUtc: "04:00", EnabledSince: since));
    }

    [Fact]
    public async Task Disabling_a_policy_clears_enabled_since()
    {
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true, EnabledSince: Utc(2026, 10, 1, 9)));

        await SavePolicy().HandleAsync(new SaveBackupPolicyCommand(_connectionId, Target, new BackupPolicy(Enabled: false, EnabledSince: Utc(2026, 10, 1, 9))));

        (await BackupPolicyStore.ReadAsync(_store, _connectionId))!.EnabledSince.Should().BeNull();
    }

    [Fact]
    public async Task An_invalid_policy_is_refused_before_anything_is_stored_or_audited()
    {
        var result = await SavePolicy().HandleAsync(new SaveBackupPolicyCommand(_connectionId, Target, new BackupPolicy(DailyAtUtc: "7pm")));

        result.Error.Should().Be("Daily time must be HH:mm (UTC), e.g. 02:00.");
        _store.Values.Should().BeEmpty();
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_policy_for_an_unknown_connection_is_refused()
    {
        (await SavePolicy().HandleAsync(new SaveBackupPolicyCommand(_unknownId, Target, BackupPolicy.Default))).Error.Should().Be(HandlerRunner.ConnectionNotFound);
        _store.Values.Should().BeEmpty();
    }
}
