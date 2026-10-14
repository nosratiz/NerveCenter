using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;
using OverviewPage = SbConsole.Plugins.Sqlite.Pages.Overview;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

/// <summary>Overview on a failed integrity check (mockup 1g, design spec §11.6): REINDEX and Recover into new file.</summary>
public class OverviewRepairPageTests : SqlitePageTestBase
{
    private const string WriteSecret = "Path=/data/reports.db;Mode=ReadWrite;BackupDir=/data/backups";
    private const string NoBackupSecret = "Path=/data/reports.db;Mode=ReadWrite";

    private static readonly IntegrityResult Failed = new(false,
        ["*** in database main ***", "Page 48112: btreeInitPage() returns error code 11", "row 2041 missing from index idx_events_ts"],
        0, new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(41));

    private string _secret = WriteSecret;

    private async Task Arrange(bool readWrite = true, bool prod = false, bool backups = true, IntegrityResult? integrity = null)
    {
        _secret = !readWrite ? Secret : backups ? WriteSecret : NoBackupSecret;
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", prod ? ["prod"] : [], prod, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = readWrite ? "read-write" : "read-only" }),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(_secret);
        Operations.GetOverviewAsync(_secret, Arg.Any<CancellationToken>()).Returns(new SqliteOverview(
            "/data/reports.db", "3.53.3", 2_254_857_830, null, Time.GetUtcNow(), 4096, 550_502, 0, "WAL", "NORMAL", 1000, -2000, 5000));
        Operations.GetTableSizesAsync(_secret, Arg.Any<CancellationToken>()).Returns(new TableSizes(false, 0, 0, []));
        await IntegrityResultStore.WriteAsync(Store, ConnectionId, integrity ?? Failed);
    }

    private IRenderedComponent<OverviewPage> RenderPage()
    {
        var cut = Render<OverviewPage>();
        cut.WaitForElement(".sqlite-overview-integrity-errors");
        return cut;
    }

    [Fact]
    public async Task A_read_write_connection_with_backups_offers_reindex_and_recover()
    {
        await Arrange();

        var cut = RenderPage();

        cut.Find(".sqlite-overview-reindex").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".sqlite-overview-recover").HasAttribute("disabled").Should().BeFalse();
        cut.Find(".sqlite-overview-repair-note").TextContent.Should().Contain("Recover into new file").And.NotContain("arrive with write mode");
    }

    [Fact]
    public async Task Without_a_backup_directory_recover_is_disabled_with_the_reason()
    {
        await Arrange(backups: false);

        var cut = RenderPage();

        cut.Find(".sqlite-overview-recover").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-overview-recover-wrap").GetAttribute("title").Should().Be(SqliteOperations.BackupsNotEnabled);
        cut.Find(".sqlite-overview-reindex").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task A_read_only_connection_explains_both_need_read_write_and_shows_no_buttons()
    {
        await Arrange(readWrite: false);

        var cut = RenderPage();

        cut.FindAll(".sqlite-overview-reindex").Should().BeEmpty();
        cut.FindAll(".sqlite-overview-recover").Should().BeEmpty();
        cut.Find(".sqlite-overview-repair-readonly").TextContent.Should().Contain("Default mode Read-write");
    }

    [Fact]
    public async Task A_passing_check_offers_no_repair()
    {
        await Arrange(integrity: Failed with { IntegrityOk = true, Messages = ["ok"] });

        var cut = Render<OverviewPage>();
        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("ok"));

        cut.FindAll(".sqlite-overview-reindex").Should().BeEmpty();
    }

    [Fact]
    public async Task Reindex_without_prod_runs_without_confirming_and_shows_the_fresh_result()
    {
        await Arrange();
        Operations.ReindexAsync(_secret, Arg.Any<CancellationToken>()).Returns(new IntegrityCheckOutcome(["ok"], 0));
        var cut = RenderPage();

        await cut.Find(".sqlite-overview-reindex").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("ok"));
        cut.Find(".sqlite-overview-reindex-result").TextContent.Should().Contain("fresh integrity check is ok");
        await Confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("sqlite.reindex", ConnectionName, ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reindex_on_prod_needs_the_typed_confirm_and_a_declined_one_runs_nothing()
    {
        await Arrange(prod: true);
        Confirmation.ConfirmAsync("Reindex", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        await cut.Find(".sqlite-overview-reindex").ClickAsync(new());

        await Confirmation.Received(1).ConfirmAsync("Reindex", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().ReindexAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reindex_that_leaves_page_errors_says_they_need_recover()
    {
        await Arrange();
        Operations.ReindexAsync(_secret, Arg.Any<CancellationToken>())
            .Returns(new IntegrityCheckOutcome(["*** in database main ***", "Page 48112: btreeInitPage() returns error code 11"], 0));
        var cut = RenderPage();

        await cut.Find(".sqlite-overview-reindex").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-reindex-result").TextContent.Should().Contain("2 errors remain").And.Contain("Recover into new file"));
        cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("2 errors");
    }

    [Fact]
    public async Task Recover_shows_the_new_file_counts_and_failures_inline()
    {
        await Arrange();
        Operations.SalvageAsync(_secret, Time.GetUtcNow(), Arg.Any<CancellationToken>()).Returns(new SalvageOutcome(
            "reports-recovered-20261013-094051.db", 6, 1_204_117, 2, 9,
            [new SalvageFailure("table", "events", "stopped after 41,000 rows — The file is not a valid or healthy SQLite database.")],
            TimeSpan.FromSeconds(12)));
        var cut = RenderPage();

        await cut.Find(".sqlite-overview-recover").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-recover-file").TextContent.Should().Be("reports-recovered-20261013-094051.db"));
        cut.Find(".sqlite-overview-recover-counts").TextContent.Should().Contain("6 tables · 1,204,117 rows · 2 rows skipped");
        cut.Find(".sqlite-overview-recover-failure").TextContent.Should().Contain("events").And.Contain("stopped after 41,000 rows");
        await Audit.Received(1).RecordAsync("sqlite.recover", ConnectionName, ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_recover_shows_the_friendly_error()
    {
        await Arrange();
        Operations.SalvageAsync(_secret, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<SalvageOutcome>>(_ => throw new SqliteBackupException("Backup directory does not exist: /data/backups"));
        var cut = RenderPage();

        await cut.Find(".sqlite-overview-recover").ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-repair-error").TextContent.Should().Contain("Backup directory does not exist"));
        cut.FindAll(".sqlite-overview-recover-result").Should().BeEmpty();
    }
}
