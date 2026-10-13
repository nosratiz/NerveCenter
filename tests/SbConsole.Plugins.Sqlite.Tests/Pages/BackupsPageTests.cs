using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;
using BackupsPage = SbConsole.Plugins.Sqlite.Pages.Backups;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

/// <summary>The Backups page (mockups 1e / 1f restore, design spec §11.7).</summary>
public class BackupsPageTests : SqlitePageTestBase
{
    private const string ReadOnlySecret = "Path=/data/reports.db;BackupDir=/data/backups";
    private const string WriteSecret = "Path=/data/reports.db;Mode=ReadWrite;BackupDir=/data/backups";
    private const string DiskFull = "The disk or volume is full.";
    private const long LiveBytes = 2_254_857_830;
    private const long BackupBytes = LiveBytes - (260L * 1024 * 1024);

    private static readonly SqliteFileFingerprint Live = new(LiveBytes, 6, 41_200_000, "fp-live",
        ["index|idx_events_ts|aaa", "table|events|bbb", "table|tenants|ccc"]);

    private readonly BackupRecord _failed;
    private readonly BackupRecord _nightly;
    private readonly BackupRecord _manual;
    private readonly BackupRecord _unverified;
    private readonly BackupRecord _may;
    private readonly BackupRecord _april;
    private string _secret = WriteSecret;

    public BackupsPageTests()
    {
        _failed = new BackupRecord(Guid.NewGuid(), null, Utc(10, 13, 2), 0, 900, BackupRecord.TriggerSchedule, "system", null,
            BackupRecord.StatusFailed, DiskFull);
        _nightly = Ok(Utc(10, 12, 2)) with
        {
            Verify = BackupRecord.VerifyOk, Sha256 = "9c1e" + new string('0', 56) + "b2a4", Tables = 6, RowsApprox = 40_000_000,
            SchemaFingerprint = "fp-live", VerifiedAt = Utc(10, 12, 2, 3),
        };
        _manual = Ok(Utc(10, 11, 14, 22), BackupRecord.TriggerManual) with
        {
            Actor = "admin", Note = "before migration 0038", Verify = BackupRecord.VerifyOk, SchemaFingerprint = "fp-old", RowsApprox = 39_000_000,
        };
        _unverified = Ok(Utc(10, 10, 2));
        _may = Ok(Utc(5, 1, 2)) with { Verify = BackupRecord.VerifyOk };
        _april = Ok(Utc(4, 1, 2)) with { Verify = BackupRecord.VerifyOk };
    }

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static BackupRecord Ok(DateTimeOffset at, string trigger = BackupRecord.TriggerSchedule) =>
        new(Guid.NewGuid(), $"reports-{at:yyyyMMdd-HHmmss}.db", at, BackupBytes, 134_000, trigger, "system", null, BackupRecord.StatusOk, null);

    private IReadOnlyList<BackupRecord> All => [_failed, _nightly, _manual, _unverified, _may, _april];

    private async Task Arrange(bool readWrite = true, bool prod = false, bool backups = true, IReadOnlyList<BackupRecord>? history = null, bool policy = true)
    {
        _secret = !backups ? Secret : readWrite ? WriteSecret : ReadOnlySecret;
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", prod ? ["prod"] : [], prod, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = readWrite ? "read-write" : "read-only" }),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(_secret);
        Operations.GetLiveFingerprintAsync(_secret, Arg.Any<CancellationToken>()).Returns(Live);
        await BackupHistoryStore.WriteAsync(Store, ConnectionId, history ?? All);
        if (policy)
        {
            await BackupPolicyStore.WriteAsync(Store, ConnectionId, new BackupPolicy(Enabled: true, DailyAtUtc: "02:00"));
        }
    }

    private IRenderedComponent<BackupsPage> RenderPage()
    {
        var cut = Render<BackupsPage>();
        cut.WaitForElement(".sqlite-backups-history, .sqlite-backups-disabled, .sqlite-backups-error, .sqlite-backups-empty");
        return cut;
    }

    private static void Select(IRenderedComponent<BackupsPage> cut, BackupRecord record)
    {
        cut.Find($".sqlite-backup-row[data-id='{record.Id}']").Click();
        cut.WaitForElement(".sqlite-backup-detail");
    }

    // ---- header, tiles, history ----

    [Fact]
    public async Task Header_meta_names_the_target_the_kept_count_and_total_size()
    {
        await Arrange();

        var cut = RenderPage();

        cut.Find(".sqlite-backups-meta").TextContent.Should().Be($"target /data/backups · 5 kept · {SqliteFormat.Bytes(5 * BackupBytes)}");
    }

    [Fact]
    public async Task Tiles_show_last_success_method_verified_and_retention()
    {
        await Arrange();

        var cut = RenderPage();

        cut.Find(".sqlite-backups-last-age").TextContent.Should().Be("31h ago");
        cut.Find(".sqlite-backups-last").TextContent.Should().Contain("policy: daily 02:00 UTC").And.Contain("missed 4");
        cut.Find(".sqlite-backups-method").TextContent.Should().Contain("VACUUM INTO").And.Contain("compacts on the way out");
        cut.Find(".sqlite-backups-verified-count").TextContent.Should().Be("4 / 5");
        cut.Find(".sqlite-backups-retention-value").TextContent.Should().Be("7d · 4w · 3m");
        cut.Find(".sqlite-backups-retention").TextContent.Should().Contain("next prune removes 2");
    }

    [Fact]
    public async Task Without_a_policy_the_last_success_tile_says_no_schedule()
    {
        await Arrange(policy: false);

        var cut = RenderPage();

        cut.Find(".sqlite-backups-last").TextContent.Should().Contain("no schedule").And.NotContain("missed");
    }

    [Fact]
    public async Task History_lists_every_record_newest_first_with_verify_badges_triggers_and_errors()
    {
        await Arrange();

        var cut = RenderPage();

        var rows = cut.FindAll(".sqlite-backup-row");
        rows.Should().HaveCount(6);
        rows[0].GetAttribute("data-id").Should().Be(_failed.Id.ToString());
        rows[0].QuerySelector(".sqlite-backup-verify")!.TextContent.Should().Be("failed");
        rows[0].QuerySelector(".sqlite-backup-error")!.TextContent.Should().Be(DiskFull);
        rows[0].QuerySelector(".sqlite-backup-size")!.TextContent.Should().Be("—");
        rows[1].QuerySelector(".sqlite-backup-taken")!.TextContent.Should().Be("2026-10-12 02:00");
        rows[1].QuerySelector(".sqlite-backup-size")!.TextContent.Should().Be(SqliteFormat.Bytes(BackupBytes));
        rows[1].QuerySelector(".sqlite-backup-took")!.TextContent.Should().Be("2m 14s");
        rows[1].QuerySelector(".sqlite-backup-verify")!.TextContent.Should().Be("ok");
        rows[2].QuerySelector(".sqlite-backup-trigger")!.TextContent.Should().Be("manual · admin · before migration 0038");
        rows[3].QuerySelector(".sqlite-backup-verify")!.TextContent.Should().Be("unverified");
    }

    [Fact]
    public async Task An_empty_history_says_how_to_take_the_first_backup()
    {
        await Arrange(history: []);

        var cut = RenderPage();

        cut.Find(".sqlite-backups-empty").TextContent.Should().Contain("No backups yet");
        cut.Find(".sqlite-backups-last-age").TextContent.Should().Be("none yet");
    }

    [Fact]
    public async Task Without_a_backup_directory_the_page_explains_how_to_enable_backups()
    {
        await Arrange(backups: false);

        var cut = RenderPage();

        var text = cut.Find(".sqlite-backups-disabled").TextContent;
        text.Should().Contain("Enable backups to").And.Contain("SBC_SQLITE_ROOTS").And.Contain("not a root itself");
        cut.Find(".sqlite-backups-now").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-backups-schedule").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".sqlite-backups-history").Should().BeEmpty();
    }

    [Fact]
    public async Task A_load_failure_shows_the_banner_with_retry_and_connection_settings()
    {
        await Arrange();
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var cut = RenderPage();

        cut.Find(".sqlite-backups-error-text").TextContent.Should().NotBeEmpty();
        cut.Find(".sqlite-backups-error a").GetAttribute("href").Should().Be("/connections");
        cut.FindAll(".sqlite-backups-retry").Should().HaveCount(1);
    }

    // ---- detail panel ----

    [Fact]
    public async Task Selecting_a_row_shows_the_detail_and_the_comparison_with_the_live_file()
    {
        await Arrange();
        var cut = RenderPage();

        Select(cut, _nightly);

        var detail = cut.Find(".sqlite-backup-detail");
        cut.Find(".sqlite-backup-detail-file").TextContent.Should().Be(_nightly.FileName);
        detail.TextContent.Should().Contain("sha256 9c1e…b2a4").And.Contain("6 tables").And.Contain("40.0M rows ≈").And.Contain("integrity_check ok");
        cut.Find(".sqlite-backup-compare").TextContent.Should().Contain("−260 MB").And.Contain("−1.2M rows ≈");
        cut.Find(".sqlite-backup-schema").TextContent.Should().Be("schema identical");
        cut.Find(".sqlite-backup-open").GetAttribute("href").Should()
            .Be($"/p/sqlite/query?connectionId={ConnectionId}&backup={Uri.EscapeDataString(_nightly.FileName!)}");
    }

    [Fact]
    public async Task A_different_schema_fingerprint_says_the_schema_differs()
    {
        await Arrange();
        var cut = RenderPage();

        Select(cut, _manual);

        cut.Find(".sqlite-backup-schema").TextContent.Should().Be("schema differs — Verify again counts the objects");
    }

    [Fact]
    public async Task Verify_again_reverifies_and_counts_the_differing_schema_objects()
    {
        await Arrange();
        Operations.VerifyBackupAsync(_secret, _manual.FileName!, Arg.Any<CancellationToken>()).Returns(new BackupVerification(true, ["ok"], new string('a', 64),
            new SqliteFileFingerprint(BackupBytes, 5, 39_000_000, "fp-old", ["index|idx_events_ts|aaa", "table|events|zzz"])));
        var cut = RenderPage();
        Select(cut, _manual);

        cut.Find(".sqlite-backup-verify-again").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-backup-schema").TextContent.Should().Be("2 objects differ"));
        await Audit.Received(1).RecordAsync("sqlite.backup.verify", ConnectionName, ActionRisk.Safe, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_record_shows_its_error_and_no_file_actions()
    {
        await Arrange();
        var cut = RenderPage();

        Select(cut, _failed);

        cut.Find(".sqlite-backup-detail").TextContent.Should().Contain(DiskFull);
        cut.FindAll(".sqlite-backup-download").Should().BeEmpty();
        cut.FindAll(".sqlite-backup-restore").Should().BeEmpty();
    }

    [Fact]
    public async Task Download_streams_the_backup_through_the_browser_and_is_audited()
    {
        await Arrange();
        Operations.OpenBackupStreamAsync(_secret, _nightly.FileName!, Arg.Any<CancellationToken>())
            .Returns(_ => new BackupFileStream(_nightly.FileName!, new MemoryStream(Encoding.UTF8.GetBytes("SQLite format 3")), 15));
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Sqlite/sqlite.js");
        module.SetupVoid("downloadFromStream", _ => true).SetVoidResult();
        var cut = RenderPage();
        Select(cut, _nightly);

        cut.Find(".sqlite-backup-download-note").TextContent.Should().Contain("over 1 GB");
        cut.Find(".sqlite-backup-download").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-backup-message").TextContent.Should().Be($"Downloaded {_nightly.FileName}."));
        module.VerifyInvoke("downloadFromStream").Arguments[0].Should().Be(_nightly.FileName);
        await Audit.Received(1).RecordAsync("sqlite.backup.download", ConnectionName, ActionRisk.Safe, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- restore ----

    [Fact]
    public async Task Restore_is_disabled_with_the_reason_on_a_read_only_connection()
    {
        await Arrange(readWrite: false);
        var cut = RenderPage();
        Select(cut, _nightly);

        cut.Find(".sqlite-backup-restore").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-backup-restore-wrap").GetAttribute("title").Should().Contain("read-only");
    }

    [Fact]
    public async Task Restore_dialog_states_the_live_path_the_backup_and_what_will_be_lost()
    {
        await Arrange();
        var cut = RenderPage();
        Select(cut, _nightly);

        cut.Find(".sqlite-backup-restore").Click();

        var dialog = cut.Find(".sqlite-restore-dialog");
        dialog.TextContent.Should().Contain("/data/reports.db").And.Contain(_nightly.FileName).And.Contain("reports.db.pre-restore-");
        cut.Find(".sqlite-restore-loss").TextContent.Should().Be("You will lose 31 hours of writes ≈ 1.2M rows since the backup was taken.");
    }

    [Fact]
    public async Task Restore_confirms_with_the_typed_name_on_prod_and_declining_restores_nothing()
    {
        await Arrange(prod: true);
        Confirmation.ConfirmAsync("Restore backup over", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();
        Select(cut, _nightly);
        cut.Find(".sqlite-backup-restore").Click();

        cut.Find(".sqlite-restore-submit").Click();

        await Confirmation.Received(1).ConfirmAsync("Restore backup over", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().RestoreBackupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        cut.FindAll(".sqlite-restore-dialog").Should().HaveCount(1);
    }

    [Fact]
    public async Task A_confirmed_restore_runs_and_shows_the_outcome()
    {
        await Arrange();
        Operations.RestoreBackupAsync(_secret, _nightly.FileName!, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreOutcome("reports.db.pre-restore-20261013-094051", true, ["ok"], TimeSpan.FromSeconds(134)));
        var cut = RenderPage();
        Select(cut, _nightly);
        cut.Find(".sqlite-backup-restore").Click();

        cut.Find(".sqlite-restore-submit").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-restore-result").TextContent.Should().Contain(_nightly.FileName)
            .And.Contain("reports.db.pre-restore-20261013-094051").And.Contain("integrity_check ok").And.Contain("2m 14s"));
        await Confirmation.Received(1).ConfirmAsync("Restore backup over", ConnectionName, false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("sqlite.backup.restore", ConnectionName, ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        cut.FindAll(".sqlite-restore-dialog").Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_restore_stays_in_the_dialog_with_the_error()
    {
        await Arrange();
        Operations.RestoreBackupAsync(_secret, _nightly.FileName!, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SqliteBackupException("The backup failed verification — restore cancelled."));
        var cut = RenderPage();
        Select(cut, _nightly);
        cut.Find(".sqlite-backup-restore").Click();

        cut.Find(".sqlite-restore-submit").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-restore-dialog .sqlite-dialog-error").TextContent.Should().Contain("restore cancelled"));
    }

    // ---- back up now, schedule, prune ----

    [Fact]
    public async Task Back_up_now_with_a_note_runs_the_backup_and_selects_the_new_record()
    {
        await Arrange();
        Operations.CreateBackupAsync(_secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new BackupCreated("reports-20261013-094051.db", BackupBytes, TimeSpan.FromSeconds(130)));
        Operations.VerifyBackupAsync(_secret, "reports-20261013-094051.db", Arg.Any<CancellationToken>())
            .Returns(new BackupVerification(true, ["ok"], new string('b', 64), Live));
        var cut = RenderPage();

        cut.Find(".sqlite-backups-now").Click();
        cut.Find(".sqlite-backup-note").Input("before migration 0039");
        cut.Find(".sqlite-backup-now-submit").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-backups-notice").TextContent.Should().Contain("reports-20261013-094051.db").And.Contain("verified ok"));
        cut.Find(".sqlite-backup-detail-file").TextContent.Should().Be("reports-20261013-094051.db");
        var newest = (await BackupHistoryStore.ReadAsync(Store, ConnectionId))[0];
        newest.Note.Should().Be("before migration 0039");
        newest.Trigger.Should().Be(BackupRecord.TriggerManual);
        await Audit.Received(1).RecordAsync("sqlite.backup.create", ConnectionName, ActionRisk.Safe, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_backup_shows_the_error()
    {
        await Arrange();
        Operations.CreateBackupAsync(_secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SqliteException("full", 13));
        var cut = RenderPage();

        cut.Find(".sqlite-backups-now").Click();
        cut.Find(".sqlite-backup-now-submit").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-dialog-error").TextContent.Should().Be(DiskFull));
    }

    [Fact]
    public async Task Schedule_refuses_an_invalid_time_without_saving()
    {
        await Arrange(policy: false);
        var cut = RenderPage();

        cut.Find(".sqlite-backups-schedule").Click();
        cut.Find(".sqlite-schedule-time").Input("25:00");
        cut.Find(".sqlite-schedule-save").Click();

        cut.Find(".sqlite-schedule-dialog .sqlite-dialog-error").TextContent.Should().Be("Daily time must be HH:mm (UTC), e.g. 02:00.");
        (await BackupPolicyStore.ReadAsync(Store, ConnectionId)).Should().BeNull();
    }

    [Fact]
    public async Task Schedule_saves_the_policy_and_the_tiles_follow()
    {
        await Arrange(policy: false);
        var cut = RenderPage();

        cut.Find(".sqlite-backups-schedule").Click();
        cut.Find(".sqlite-schedule-enabled").Change(true);
        cut.Find(".sqlite-schedule-time").Input("03:30");
        cut.Find(".sqlite-schedule-daily").Input("10");
        cut.Find(".sqlite-schedule-weekly").Input("2");
        cut.Find(".sqlite-schedule-monthly").Input("6");
        cut.Find(".sqlite-schedule-save").Click();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-schedule-dialog").Should().BeEmpty());
        (await BackupPolicyStore.ReadAsync(Store, ConnectionId)).Should().Be(new BackupPolicy(true, "03:30", 10, 2, 6));
        cut.Find(".sqlite-backups-retention-value").TextContent.Should().Be("10d · 2w · 6m");
        cut.Find(".sqlite-backups-last").TextContent.Should().Contain("policy: daily 03:30 UTC");
        await Audit.Received(1).RecordAsync("sqlite.backup.policy", ConnectionName, ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prune_confirms_with_the_count_and_declining_deletes_nothing()
    {
        await Arrange(prod: true);
        Confirmation.ConfirmAsync("Delete backups of", ConnectionName, true, 2, Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        cut.Find(".sqlite-backups-prune").Click();

        await Confirmation.Received(1).ConfirmAsync("Delete backups of", ConnectionName, true, 2, Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().DeleteBackupFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_confirmed_prune_deletes_the_planned_files_and_reloads()
    {
        await Arrange();
        var cut = RenderPage();

        cut.Find(".sqlite-backups-prune").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-backups-notice").TextContent.Should().StartWith("Pruned 2 backups"));
        await Operations.Received(1).DeleteBackupFileAsync(_secret, _may.FileName!, Arg.Any<CancellationToken>());
        await Operations.Received(1).DeleteBackupFileAsync(_secret, _april.FileName!, Arg.Any<CancellationToken>());
        cut.FindAll(".sqlite-backup-row").Should().HaveCount(4);
        cut.Find(".sqlite-backups-prune").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_newest_backup_shows_the_banner_and_disk_full_offers_prune_and_retry()
    {
        await Arrange();
        Operations.CreateBackupAsync(_secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new BackupCreated("reports-20261013-094051.db", BackupBytes, TimeSpan.FromSeconds(130)));
        Operations.VerifyBackupAsync(_secret, "reports-20261013-094051.db", Arg.Any<CancellationToken>())
            .Returns(new BackupVerification(true, ["ok"], new string('b', 64), Live));
        var cut = RenderPage();

        cut.Find(".sqlite-backups-failed").TextContent.Should().Contain("Last backup failed").And.Contain(DiskFull);
        cut.Find(".sqlite-backups-prune-retry").TextContent.Should().Be("Prune 2 and retry");
        cut.Find(".sqlite-backups-prune-retry").Click();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-backups-failed").Should().BeEmpty());
        await Confirmation.Received(1).ConfirmAsync("Delete backups of", ConnectionName, false, 2, Arg.Any<CancellationToken>());
        await Operations.Received(2).DeleteBackupFileAsync(_secret, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Operations.Received(1).CreateBackupAsync(_secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failure_that_is_not_disk_full_offers_no_prune_and_retry()
    {
        await Arrange(history: [_failed with { Error = "The file is locked." }, _nightly]);

        var cut = RenderPage();

        cut.Find(".sqlite-backups-failed").TextContent.Should().Contain("The file is locked.");
        cut.FindAll(".sqlite-backups-prune-retry").Should().BeEmpty();
    }
}
