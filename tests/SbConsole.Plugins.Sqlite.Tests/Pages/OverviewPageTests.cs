using Bunit;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

// The page type is fully qualified: this namespace has SbConsole.Plugins.Sqlite as an ancestor,
// whose nested "Overview" namespace would win over a using directive (same as Redis's OverviewPageTests).
public class OverviewPageTests : SqlitePageTestBase
{
    private static SqliteOverview Sample(long freelist = 46_387, long? wal = 50_331_648) => new(
        "/data/reports.db", "3.53.3", 2_254_857_830, wal, new DateTimeOffset(2026, 10, 13, 9, 40, 51, TimeSpan.Zero),
        4096, 550_502, freelist, "WAL", "NORMAL", 1000, -2000, 5000);

    private static readonly TableSizes NoDbstat = new(false, 6, 19,
    [
        new TableSizeEntry("event_props", SchemaObjectType.Table, null, 88_000_000),
        new TableSizeEntry("events", SchemaObjectType.Table, null, 41_200_000),
        new TableSizeEntry("sessions", SchemaObjectType.Table, null, 2_418_902),
        new TableSizeEntry("users", SchemaObjectType.Table, null, 184_220),
        new TableSizeEntry("tenants", SchemaObjectType.Table, null, 412),
        new TableSizeEntry("schedules", SchemaObjectType.Table, null, null),
    ]);

    private void Arrange(SqliteOverview? overview = null, TableSizes? sizes = null)
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(overview ?? Sample());
        Operations.GetTableSizesAsync(Secret, Arg.Any<CancellationToken>()).Returns(sizes ?? NoDbstat);
    }

    private async Task<IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Overview>> RenderPageAsync()
    {
        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Overview>();
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Renders_the_file_journal_and_page_cache_tiles()
    {
        Arrange();

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-meta").TextContent.Should().Be("/data/reports.db · 3.53.3 · WAL");
        cut.Find(".sqlite-overview-file-size").TextContent.Should().Contain("2.1 GB").And.Contain("+ 48 MB -wal");
        cut.Find(".sqlite-overview-file").TextContent.Should().Contain("page 4 KB · 550,502 pages")
            .And.Contain("8% free pages (181 MB)").And.Contain("VACUUM").And.Contain("2026-10-13 09:40:51 UTC");
        cut.Find(".sqlite-overview-journal-mode").TextContent.Should().Be("WAL");
        cut.Find(".sqlite-overview-journal").TextContent.Should().Contain("synchronous NORMAL").And.Contain("checkpoint every 1,000 pages");
        cut.Find(".sqlite-overview-cache-pages").TextContent.Should().Be("500 p");
        cut.Find(".sqlite-overview-busy").TextContent.Should().Be("5,000 ms");
    }

    [Fact]
    public async Task A_file_without_free_pages_says_so()
    {
        Arrange(Sample(freelist: 0, wal: null));

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-free").TextContent.Should().Contain("no free pages");
        cut.FindAll(".sqlite-overview-wal").Should().BeEmpty();
    }

    [Fact]
    public async Task Without_dbstat_the_panel_ranks_the_largest_tables_by_estimated_rows()
    {
        Arrange();

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-sizes .sql-card-title").TextContent.Should().Be("Largest tables");
        cut.Find(".sqlite-overview-sizes .sql-card-sub").TextContent.Should().Be("sqlite_stat1 · 6 tables, 19 indexes");
        var rows = cut.FindAll(".sqlite-overview-size-row");
        rows.Should().HaveCount(4);
        rows[0].TextContent.Should().Contain("event_props").And.Contain("88.0M rows ≈");
        cut.Find(".sqlite-overview-size-more").TextContent.Should().Contain("2 more");
        cut.Find(".sqlite-overview-no-dbstat").TextContent.Should().Contain("dbstat");
    }

    [Fact]
    public async Task With_dbstat_the_panel_shows_bytes_and_marks_indexes()
    {
        Arrange(sizes: new TableSizes(true, 1, 1,
        [
            new TableSizeEntry("events", SchemaObjectType.Table, 1_395_864_371, 41_200_000),
            new TableSizeEntry("idx_events_ts", SchemaObjectType.Index, 100_663_296, null),
        ]));

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-sizes .sql-card-title").TextContent.Should().Be("Size by table");
        var rows = cut.FindAll(".sqlite-overview-size-row");
        rows[0].TextContent.Should().Contain("1.3 GB").And.Contain("41.2M rows ≈");
        rows[1].TextContent.Should().Contain("96 MB").And.Contain("index");
        cut.FindAll(".sqlite-overview-no-dbstat").Should().BeEmpty();
    }

    [Fact]
    public async Task The_integrity_strip_says_never_run_until_a_result_is_stored()
    {
        Arrange();

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("never run");
    }

    [Fact]
    public async Task The_integrity_strip_shows_the_last_stored_result()
    {
        Arrange();
        await IntegrityResultStore.WriteAsync(Store, ConnectionId,
            new IntegrityResult(true, ["ok"], 0, new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(41)));

        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("ok");
        cut.Find(".sqlite-overview-integrity-meta").TextContent.Should()
            .Contain("started 2026-10-13 02:00 UTC").And.Contain("took 41 s").And.Contain("foreign_key_check 0 violations");
    }

    [Fact]
    public async Task Run_now_runs_the_check_audits_it_and_lists_the_errors()
    {
        Arrange();
        Operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>()).Returns(new IntegrityCheckOutcome(
            ["*** in database main ***", "row 2041 missing from index idx_events_ts"], 0));
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-run-integrity").Click();
        await SettleAsync(cut);

        cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("2 errors");
        cut.Find(".sqlite-overview-integrity-errors").TextContent.Should().Contain("row 2041 missing from index idx_events_ts");
        await Audit.Received(1).RecordAsync("sqlite.integrity.check", ConnectionName, ActionRisk.Safe, true,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_load_shows_the_friendly_error_and_retry_recovers()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<SqliteOverview>>(_ => throw new SqliteException("unable to open database file", 14));
        Operations.GetTableSizesAsync(Secret, Arg.Any<CancellationToken>()).Returns(NoDbstat);
        var cut = await RenderPageAsync();

        cut.Find(".sqlite-overview-error").TextContent.Should().Contain("File not found, or not readable by the SbConsole container user.");

        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Sample());
        cut.Find(".sqlite-overview-retry").Click();
        await SettleAsync(cut);

        cut.FindAll(".sqlite-overview-error").Should().BeEmpty();
        cut.Find(".sqlite-overview-file-size").TextContent.Should().Contain("2.1 GB");
    }
}
