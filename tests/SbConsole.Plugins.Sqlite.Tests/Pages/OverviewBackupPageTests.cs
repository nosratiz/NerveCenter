using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;
using OverviewPage = SbConsole.Plugins.Sqlite.Pages.Overview;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

/// <summary>Overview's Last backup tile and Growth — 30 days card (mockup 1a, design spec §11.8).</summary>
public class OverviewBackupPageTests : SqlitePageTestBase
{
    private const string BackupSecret = "Path=/data/reports.db;BackupDir=/data/backups";
    private const long Mb = 1024 * 1024;

    private string _secret = BackupSecret;

    private void Arrange(bool backups = true)
    {
        _secret = backups ? BackupSecret : Secret;
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(_secret);
        Operations.GetOverviewAsync(_secret, Arg.Any<CancellationToken>()).Returns(new SqliteOverview(
            "/data/reports.db", "3.53.3", 2_254_857_830, null, Time.GetUtcNow(), 4096, 550_502, 0, "WAL", "NORMAL", 1000, -2000, 5000));
        Operations.GetTableSizesAsync(_secret, Arg.Any<CancellationToken>()).Returns(new TableSizes(false, 0, 0, []));
    }

    private static BackupRecord Ok(DateTimeOffset at) =>
        new(Guid.NewGuid(), $"reports-{at:yyyyMMdd-HHmmss}.db", at, 1000, 50, BackupRecord.TriggerSchedule, "system", null, BackupRecord.StatusOk, null);

    private IRenderedComponent<OverviewPage> RenderPage()
    {
        var cut = Render<OverviewPage>();
        cut.WaitForElement(".sqlite-overview-file-size");
        return cut;
    }

    [Fact]
    public async Task Last_backup_tile_shows_the_age_of_the_newest_success_and_the_policy()
    {
        Arrange();
        await BackupHistoryStore.WriteAsync(Store, ConnectionId, [Ok(Time.GetUtcNow().AddHours(-31).AddMinutes(-5)), Ok(Time.GetUtcNow().AddDays(-3))]);
        await BackupPolicyStore.WriteAsync(Store, ConnectionId, new BackupPolicy(Enabled: true, DailyAtUtc: "02:00"));

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-backup-age").TextContent.Should().Be("31h ago"));
        cut.Find(".sqlite-overview-backup-policy").TextContent.Should().Be("policy: daily 02:00 UTC");
        cut.Find(".sqlite-overview-backup-now").GetAttribute("href").Should().Be($"/p/sqlite/backups?connectionId={ConnectionId}");
        cut.Find(".sqlite-overview-backup-now").TextContent.Should().Be("Back up now →");
    }

    [Fact]
    public void Last_backup_tile_says_no_schedule_and_none_yet_without_policy_or_history()
    {
        Arrange();

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-backup-age").TextContent.Should().Be("none yet"));
        cut.Find(".sqlite-overview-backup-policy").TextContent.Should().Be("no schedule");
    }

    [Fact]
    public void Last_backup_tile_says_backups_are_not_enabled_without_a_backup_directory()
    {
        Arrange(backups: false);

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-backup-policy").TextContent.Should().Be("backups not enabled"));
        cut.Find(".sqlite-overview-backup-age").TextContent.Should().Be("off");
    }

    [Fact]
    public async Task Growth_card_shows_first_to_last_delta_rate_sparkline_and_days_until_full()
    {
        Arrange();
        var now = Time.GetUtcNow();
        await GrowthStore.AppendAsync(Store, ConnectionId, new GrowthSample(now.AddDays(-30), 1843 * Mb));
        await GrowthStore.AppendAsync(Store, ConnectionId, new GrowthSample(now.AddDays(-15), 2000 * Mb));
        await GrowthStore.AppendAsync(Store, ConnectionId, new GrowthSample(now.AddMinutes(-10), 2153 * Mb));
        Operations.GetVolumeAsync(_secret, Arg.Any<CancellationToken>()).Returns(new VolumeInfo(2560 * Mb, 380 * Mb));

        var cut = RenderPage();

        cut.WaitForElement(".sqlite-overview-growth-spark");
        cut.Find(".sqlite-overview-growth-delta").TextContent.Should().Be("+310 MB · 10 MB/day");
        cut.Find(".sqlite-overview-growth-first").TextContent.Should().Be("1.8 GB");
        cut.Find(".sqlite-overview-growth-last").TextContent.Should().Be("2.1 GB");
        cut.Find(".sqlite-overview-growth-spark polyline").GetAttribute("points")!.Split(' ').Should().HaveCount(3);
        cut.Find(".sqlite-overview-growth-volume").TextContent.Should().Be("volume 2.5 GB — ~37 days at this rate");
    }

    [Fact]
    public async Task Growth_card_is_collecting_with_a_single_sample()
    {
        Arrange();
        await GrowthStore.AppendAsync(Store, ConnectionId, new GrowthSample(Time.GetUtcNow().AddMinutes(-20), 2153 * Mb));

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-growth-collecting").TextContent
            .Should().Be("collecting — first sample at 2026-10-13 09:20 UTC"));
        cut.FindAll(".sqlite-overview-growth-spark").Should().BeEmpty();
    }

    [Fact]
    public async Task A_load_takes_a_growth_sample()
    {
        Arrange();
        Operations.GetFileBytesAsync(_secret, Arg.Any<CancellationToken>()).Returns(2153 * Mb);

        var cut = RenderPage();

        cut.WaitForElement(".sqlite-overview-growth-collecting");
        (await GrowthStore.ReadAsync(Store, ConnectionId)).Should().ContainSingle().Which.Bytes.Should().Be(2153 * Mb);
    }

    [Fact]
    public void The_tiles_render_while_backups_and_growth_are_still_loading()
    {
        Arrange();
        var volume = new TaskCompletionSource<VolumeInfo?>();
        Operations.GetVolumeAsync(_secret, Arg.Any<CancellationToken>()).Returns(volume.Task);
        var fingerprint = new TaskCompletionSource<SqliteFileFingerprint>();
        Operations.GetLiveFingerprintAsync(_secret, Arg.Any<CancellationToken>()).Returns(fingerprint.Task);

        var cut = RenderPage();

        cut.Find(".sqlite-overview-backup-age").TextContent.Should().Be("…");
        cut.Find(".sqlite-overview-growth").TextContent.Should().Contain("Reading samples…");
        volume.SetResult(null);
        fingerprint.SetResult(new SqliteFileFingerprint(1, 1, null, "x", []));
        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-backup-age").TextContent.Should().Be("none yet"));
    }
}
