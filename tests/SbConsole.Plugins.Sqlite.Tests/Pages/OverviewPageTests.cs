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

    private IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Overview> RenderPage() =>
        Render<SbConsole.Plugins.Sqlite.Pages.Overview>();

    private const string SecretB = "Path=/data/scratch.db";
    private readonly Guid _secondId = Guid.NewGuid();

    private void TwoConnections()
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", ["prod"], Summary: new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-only" }),
            new ConnectionInfo(_secondId, "sqlite-scratch", "sqlite", [], Summary: new Dictionary<string, string> { ["File"] = "scratch.db", ["Mode"] = "read-only" }),
        ]);
        Connections.GetSecretAsync(_secondId, Arg.Any<CancellationToken>()).Returns(SecretB);
        Operations.GetTableSizesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(NoDbstat);
    }

    // The click is deliberately not awaited: the selection handler awaits the page's load, which these tests hold open.
    private static async Task SelectSecondAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        cut.WaitForAssertion(() => cut.FindAll(".sqlite-connection-option").Should().HaveCount(2));
        _ = cut.FindAll(".sqlite-connection-option")[1].ClickAsync(new());
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPageWithPopovers() =>
        RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SbConsole.Plugins.Sqlite.Pages.Overview>(0);
            builder.CloseComponent();
        });

    [Fact]
    public void Renders_the_file_journal_and_page_cache_tiles()
    {
        Arrange();

        var cut = RenderPage();

        cut.WaitForElement(".sqlite-overview-file-size");
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
    public void A_file_without_free_pages_says_so()
    {
        Arrange(Sample(freelist: 0, wal: null));

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-free").TextContent.Should().Contain("no free pages"));
        cut.FindAll(".sqlite-overview-wal").Should().BeEmpty();
    }

    [Fact]
    public void Without_dbstat_the_panel_ranks_the_largest_tables_by_estimated_rows()
    {
        Arrange();

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-overview-size-row").Should().HaveCount(4));
        cut.Find(".sqlite-overview-sizes .sql-card-title").TextContent.Should().Be("Largest tables");
        cut.Find(".sqlite-overview-sizes .sql-card-sub").TextContent.Should().Be("sqlite_stat1 · 6 tables, 19 indexes");
        cut.FindAll(".sqlite-overview-size-row")[0].TextContent.Should().Contain("event_props").And.Contain("88.0M rows ≈");
        cut.Find(".sqlite-overview-size-more").TextContent.Should().Contain("2 more");
        cut.Find(".sqlite-overview-no-dbstat").TextContent.Should().Contain("dbstat");
    }

    [Fact]
    public void With_dbstat_the_panel_shows_bytes_and_marks_indexes()
    {
        Arrange(sizes: new TableSizes(true, 1, 1,
        [
            new TableSizeEntry("events", SchemaObjectType.Table, 1_395_864_371, 41_200_000),
            new TableSizeEntry("idx_events_ts", SchemaObjectType.Index, 100_663_296, null),
        ]));

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-overview-size-row").Should().HaveCount(2));
        cut.Find(".sqlite-overview-sizes .sql-card-title").TextContent.Should().Be("Size by table");
        var rows = cut.FindAll(".sqlite-overview-size-row");
        rows[0].TextContent.Should().Contain("1.3 GB").And.Contain("41.2M rows ≈");
        rows[1].TextContent.Should().Contain("96 MB").And.Contain("index");
        cut.FindAll(".sqlite-overview-no-dbstat").Should().BeEmpty();
    }

    [Fact]
    public void The_sizes_panel_shows_the_friendly_error_when_the_sizes_handler_fails()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Sample());
        Operations.GetTableSizesAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<TableSizes>>(_ => throw new SqliteException("unable to open database file", 14));

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-sizes-error").TextContent.Should()
            .Contain("File not found, or not readable by the SbConsole container user."));
        cut.Find(".sqlite-overview-file-size").TextContent.Should().Contain("2.1 GB");
    }

    [Fact]
    public void The_integrity_strip_says_never_run_until_a_result_is_stored()
    {
        Arrange();

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("never run"));
        cut.Find(".sqlite-overview-integrity-status").GetAttribute("role").Should().Be("status");
        cut.Find(".sqlite-overview-integrity").HasAttribute("role").Should().BeFalse();
    }

    [Fact]
    public async Task The_integrity_strip_shows_the_last_stored_result()
    {
        Arrange();
        await IntegrityResultStore.WriteAsync(Store, ConnectionId,
            new IntegrityResult(true, ["ok"], 0, new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(41)));

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("ok"));
        cut.Find(".sqlite-overview-integrity-meta").TextContent.Should()
            .Contain("started 2026-10-13 02:00 UTC").And.Contain("took 41 s").And.Contain("foreign_key_check 0 violations");
        cut.Find(".sqlite-overview-integrity").ClassList.Should().Contain("sql-strip-ok");
    }

    [Fact]
    public async Task Foreign_key_violations_warn_on_an_otherwise_ok_check_and_use_singular_grammar()
    {
        Arrange();
        await IntegrityResultStore.WriteAsync(Store, ConnectionId,
            new IntegrityResult(true, ["ok"], 1, new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(41)));

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("ok"));
        cut.Find(".sqlite-overview-integrity").ClassList.Should().Contain("sql-strip-warn").And.NotContain("sql-strip-ok");
        var meta = cut.Find(".sqlite-overview-integrity-meta").TextContent;
        meta.Should().Contain("foreign_key_check 1 violation");
        meta.Should().NotContain("1 violations");
    }

    [Fact]
    public async Task Run_now_runs_the_check_audits_it_and_lists_the_errors()
    {
        Arrange();
        Operations.RunIntegrityCheckAsync(Secret, Arg.Any<CancellationToken>()).Returns(new IntegrityCheckOutcome(
            ["*** in database main ***", "row 2041 missing from index idx_events_ts"], 0));
        var cut = RenderPage();
        cut.WaitForElement(".sqlite-overview-run-integrity");

        cut.Find(".sqlite-overview-run-integrity").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("2 errors"));
        cut.Find(".sqlite-overview-integrity-errors").TextContent.Should().Contain("row 2041 missing from index idx_events_ts");
        await Audit.Received(1).RecordAsync("sqlite.integrity.check", ConnectionName, ActionRisk.Safe, true,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_failed_load_shows_the_friendly_error_and_retry_recovers()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<SqliteOverview>>(_ => throw new SqliteException("unable to open database file", 14));
        Operations.GetTableSizesAsync(Secret, Arg.Any<CancellationToken>()).Returns(NoDbstat);
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-error").TextContent.Should()
            .Contain("File not found, or not readable by the SbConsole container user."));
        cut.Find(".sqlite-overview-error-text").GetAttribute("role").Should().Be("alert");
        cut.Find(".sqlite-overview-error").HasAttribute("role").Should().BeFalse();

        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Sample());
        cut.Find(".sqlite-overview-retry").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-file-size").TextContent.Should().Contain("2.1 GB"));
        cut.FindAll(".sqlite-overview-error").Should().BeEmpty();
    }

    [Fact]
    public void With_no_connections_the_picker_explains_and_no_tiles_render()
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(Array.Empty<ConnectionInfo>());

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-no-connections").TextContent.Should().Contain("No SQLite connections yet"));
        cut.FindAll(".sqlite-overview-file").Should().BeEmpty();
        cut.FindAll(".sqlite-overview-refresh").Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_connection_clears_the_previous_connections_data_immediately()
    {
        TwoConnections();
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Sample());
        var pendingB = new TaskCompletionSource<SqliteOverview>();
        Operations.GetOverviewAsync(SecretB, Arg.Any<CancellationToken>()).Returns(pendingB.Task);
        await IntegrityResultStore.WriteAsync(Store, ConnectionId,
            new IntegrityResult(true, ["ok"], 0, new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(41)));
        var cut = RenderPageWithPopovers();
        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-integrity-status").TextContent.Should().Be("ok"));

        await SelectSecondAsync(cut);

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-overview-file").Should().BeEmpty());
        cut.FindAll(".sqlite-overview-integrity").Should().BeEmpty();
        cut.FindAll(".sqlite-overview-meta").Should().BeEmpty();
        cut.Find(".sqlite-overview-refresh").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task A_late_response_for_the_previous_connection_neither_shows_nor_clears_the_busy_state()
    {
        TwoConnections();
        var pendingA = new TaskCompletionSource<SqliteOverview>();
        var pendingB = new TaskCompletionSource<SqliteOverview>();
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(pendingA.Task);
        Operations.GetOverviewAsync(SecretB, Arg.Any<CancellationToken>()).Returns(pendingB.Task);
        var cut = RenderPageWithPopovers();
        cut.WaitForElement(".sqlite-overview-refresh");

        await SelectSecondAsync(cut);
        var readsBefore = Store.GetCount;
        pendingA.SetResult(Sample());
        // A's continuation reads the stored integrity result right before the stale-response guard; wait for that read.
        cut.WaitForAssertion(() => Store.GetCount.Should().BeGreaterThan(readsBefore));
        await cut.InvokeAsync(() => { });

        cut.FindAll(".sqlite-overview-file").Should().BeEmpty();
        cut.Find(".sqlite-overview-refresh").HasAttribute("disabled").Should().BeTrue("B's own load is still pending");

        pendingB.SetResult(Sample() with { Path = "/data/scratch.db" });

        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-meta").TextContent.Should().StartWith("/data/scratch.db"));
        cut.WaitForAssertion(() => cut.Find(".sqlite-overview-refresh").HasAttribute("disabled").Should().BeFalse());
    }
}
