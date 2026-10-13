using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

// Page type fully qualified for the same namespace-collision reason as OverviewPageTests.
public class TablesPageTests : SqlitePageTestBase
{
    private static readonly SchemaListing Listing = new(
    [
        new SchemaObject("events", SchemaObjectType.Table, "events", 41_200_000, null),
        new SchemaObject("idx_events_tenant_ts", SchemaObjectType.Index, "events", null, null),
        new SchemaObject("sessions", SchemaObjectType.Table, "sessions", 2_418_902, null),
        new SchemaObject("tenants", SchemaObjectType.Table, "tenants", null, null),
        new SchemaObject("trg_events_ai", SchemaObjectType.Trigger, "events", null, null),
        new SchemaObject("v_daily_active", SchemaObjectType.View, "v_daily_active", null, null),
    ], SizesAvailable: false);

    private static readonly TableDetail Events = new(
        "events", SchemaObjectType.Table, "CREATE TABLE events(id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id))",
        false, 41_200_000,
        [
            new ColumnInfo("id", "INTEGER", false, null, 1, false, false),
            new ColumnInfo("session_id", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("tenant_id", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("props", "TEXT", false, null, 0, false, false),
            new ColumnInfo("ts", "INTEGER", true, null, 0, false, false),
        ],
        [new IndexInfo("idx_events_tenant_ts", false, "c", false, ["tenant_id", "ts"])],
        [new ForeignKeyInfo(0, "session_id", "sessions", "id", "NO ACTION", "CASCADE")]);

    private static readonly TableDetail Sessions = new(
        "sessions", SchemaObjectType.Table, "CREATE TABLE sessions(id INTEGER PRIMARY KEY)", false, 2_418_902,
        [new ColumnInfo("id", "INTEGER", false, null, 1, false, false)], [], []);

    private static readonly RowPage Preview = new(
        "SELECT * FROM \"events\" ORDER BY rowid DESC LIMIT 50",
        ["id", "session_id", "tenant_id", "props", "ts"],
        [
            [new CellValue(CellKind.Integer, "2", 2), new CellValue(CellKind.Integer, "2418902", 2418902), new CellValue(CellKind.Integer, "88", 88),
             new CellValue(CellKind.Text, "{\"reportId\":9106}"), new CellValue(CellKind.Integer, "1759052468112", 1759052468112)],
            [new CellValue(CellKind.Integer, "1", 1), new CellValue(CellKind.Integer, "2418902", 2418902), new CellValue(CellKind.Integer, "12", 12),
             new CellValue(CellKind.Null, "NULL"), new CellValue(CellKind.Integer, "1759052455201", 1759052455201)],
        ]);

    public TablesPageTests()
    {
        Operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>()).Returns(Listing);
        Operations.GetTableDetailAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(Events);
        Operations.GetTableDetailAsync(Secret, "sessions", Arg.Any<CancellationToken>()).Returns(Sessions);
        Operations.SampleColumnsAsync(Secret, "events", ColumnSample.DefaultSampleRows, Arg.Any<CancellationToken>()).Returns(
            [new ColumnSample("props", 4, 4, 4, 4, 4, 0, 0), new ColumnSample("ts", 4, 4, 4, 0, 0, 4, 4)]);
        Operations.SampleColumnsAsync(Secret, "sessions", ColumnSample.DefaultSampleRows, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ColumnSample>());
        Operations.PreviewRowsAsync(Secret, "events", RowPage.PreviewLimit, Arg.Any<CancellationToken>()).Returns(Preview);
        Operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(41_204_117L);
    }

    // Renders the page and waits for the first table's detail (the common case).
    private IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Tables> RenderPage()
    {
        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Tables>();
        cut.WaitForElement(".sqlite-table-name");
        return cut;
    }

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
    }

    [Fact]
    public void Lists_tables_then_views_with_estimates_and_a_schema_summary()
    {
        var cut = RenderPage();

        cut.FindAll(".sqlite-tables-row").Select(r => r.GetAttribute("data-name"))
            .Should().Equal("events", "sessions", "tenants", "v_daily_active");
        cut.Find("[data-name='events']").TextContent.Should().Contain("41.2M ≈");
        cut.Find("[data-name='tenants']").TextContent.Should().Contain("—");
        cut.Find(".sqlite-tables-summary").TextContent.Should().Be("3 tables · 1 indexes · 1 views · 1 triggers · from sqlite_schema");
    }

    [Fact]
    public void Opens_the_first_table_with_columns_badges_and_notes()
    {
        var cut = RenderPage();

        cut.Find(".sqlite-table-name").TextContent.Should().Be("events");
        cut.Find(".sqlite-table-rows").TextContent.Should().Be("41.2M rows ≈");
        cut.FindAll(".sqlite-column-row").Should().HaveCount(5);
        cut.WaitForAssertion(() => cut.Find("[data-column='props'] .sqlite-column-notes").TextContent.Should().Be("JSON in 100% of sampled rows"));
        cut.Find("[data-column='id'] .sqlite-column-notes").TextContent.Should().Be("alias of rowid");
        cut.Find("[data-column='session_id'] .sqlite-column-notes").TextContent.Should().Be("→ sessions.id · ON DELETE CASCADE");
        cut.Find("[data-column='id'] .sql-badge-pk").TextContent.Should().Be("PK");
        cut.Find("[data-column='session_id'] .sql-badge-fk").TextContent.Should().Be("FK");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("table=events");
    }

    [Fact]
    public void The_columns_indexes_and_foreign_key_tables_scroll_horizontally_inside_a_wrapper()
    {
        var cut = RenderPage();

        cut.Find(".sql-grid-wrap > table.sqlite-columns").Should().NotBeNull();
        cut.Find("[data-column='id'] .sqlite-column-notes").ClassList.Should().Contain("sql-notes");
        cut.Find(".sqlite-tab-indexes").Click();
        cut.Find(".sql-grid-wrap > table.sqlite-indexes").Should().NotBeNull();
        cut.Find(".sqlite-tab-foreignkeys").Click();
        cut.Find(".sql-grid-wrap > table.sqlite-foreign-keys").Should().NotBeNull();
    }

    [Fact]
    public void Honors_the_table_in_the_query_string()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/p/sqlite/tables?table=sessions");

        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-name").TextContent.Should().Be("sessions"));
    }

    [Fact]
    public void The_filter_narrows_the_list()
    {
        var cut = RenderPage();

        cut.Find(".sqlite-tables-filter").Input("sess");

        cut.FindAll(".sqlite-tables-row").Select(r => r.GetAttribute("data-name")).Should().Equal("sessions");
    }

    [Fact]
    public void Clicking_a_table_opens_it()
    {
        var cut = RenderPage();

        cut.Find("[data-name='sessions']").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-name").TextContent.Should().Be("sessions"));
        cut.Find("[data-name='sessions']").ClassList.Should().Contain("sql-is-selected");
    }

    [Fact]
    public void Count_exactly_replaces_the_estimate()
    {
        var cut = RenderPage();

        cut.Find(".sqlite-table-count").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-rows").TextContent.Should().Be("41,204,117 rows (exact)"));
        cut.FindAll(".sqlite-table-count").Should().BeEmpty();
    }

    [Fact]
    public void The_data_tab_shows_the_sql_cells_and_unix_ms_date_hints()
    {
        var cut = RenderPage();

        cut.Find(".sqlite-tab-data").Click();

        cut.WaitForElement(".sqlite-preview-sql");
        cut.Find(".sqlite-preview-sql").TextContent.Should().Be(Preview.Sql);
        var rows = cut.FindAll(".sqlite-preview tbody tr");
        rows.Should().HaveCount(2);
        rows[1].QuerySelectorAll("td")[3].ClassList.Should().Contain("sql-cell-null");
        rows[0].QuerySelectorAll("td")[4].TextContent.Should().Contain("1759052468112").And.Contain("2025-09-28");
        rows[0].QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("88", "tenant_id has no unix-ms note, so no hint");
    }

    [Fact]
    public void Indexes_foreign_keys_and_ddl_tabs_show_the_schema()
    {
        var cut = RenderPage();

        cut.Find(".sqlite-tab-indexes").Click();
        cut.WaitForElement(".sqlite-indexes");
        cut.Find(".sqlite-indexes").TextContent.Should().Contain("idx_events_tenant_ts").And.Contain("tenant_id, ts");

        cut.Find(".sqlite-tab-foreignkeys").Click();
        cut.Find(".sqlite-foreign-keys").TextContent.Should().Contain("sessions.id").And.Contain("CASCADE");

        cut.Find(".sqlite-tab-ddl").Click();
        cut.Find(".sqlite-ddl").TextContent.Should().StartWith("CREATE TABLE events(");
    }

    [Fact]
    public async Task Export_downloads_through_the_js_module_and_is_audited()
    {
        Operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>())
            .Returns(new CsvExport("events.csv", Encoding.UTF8.GetBytes("id\r\n1\r\n2\r\n"), 2, false));
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Sqlite/sqlite.js");
        module.SetupVoid("downloadFromStream", _ => true).SetVoidResult();
        var cut = RenderPage();

        cut.Find(".sqlite-table-export").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-export-message").TextContent.Should().Be("Exported 2 rows to events.csv."));
        module.VerifyInvoke("downloadFromStream").Arguments[0].Should().Be("events.csv");
        await Audit.Received(1).RecordAsync("sqlite.table.export", $"{ConnectionName}/events", ActionRisk.Safe, true,
            "2 rows", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_truncated_export_says_the_caps_not_a_row_count()
    {
        Operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>())
            .Returns(new CsvExport("events.csv", Encoding.UTF8.GetBytes("id\r\n1\r\n"), 1, true));
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Sqlite/sqlite.js");
        module.SetupVoid("downloadFromStream", _ => true).SetVoidResult();
        var cut = RenderPage();

        cut.Find(".sqlite-table-export").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-export-message").TextContent.Should().Be(
            "Exported the first 1 rows to events.csv — the table has more (exports are capped at 100,000 rows or 50 MB)."));
    }

    [Fact]
    public async Task Switching_connections_never_shows_the_previous_connections_tables()
    {
        TwoConnections();
        var release = new TaskCompletionSource<SchemaListing>();
        Operations.ListSchemaAsync(SecretB, Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        Operations.GetTableDetailAsync(SecretB, "scratch_only", Arg.Any<CancellationToken>()).Returns(new TableDetail(
            "scratch_only", SchemaObjectType.Table, "CREATE TABLE scratch_only(id INTEGER)", false, null,
            [new ColumnInfo("id", "INTEGER", true, null, 0, false, false)], [], []));
        Operations.SampleColumnsAsync(SecretB, "scratch_only", ColumnSample.DefaultSampleRows, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ColumnSample>());
        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SbConsole.Plugins.Sqlite.Pages.Tables>(0);
            builder.CloseComponent();
        });
        cut.WaitForElement("[data-name='events']");

        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        cut.WaitForAssertion(() => cut.FindAll(".sqlite-connection-option").Should().HaveCount(2));
        _ = cut.FindAll(".sqlite-connection-option")[1].ClickAsync(new());

        // B's listing is still in flight: A's tables must already be gone.
        cut.WaitForAssertion(() => cut.FindAll(".sqlite-tables-row").Should().BeEmpty());
        cut.FindAll(".sqlite-table-name").Should().BeEmpty();

        release.SetResult(new SchemaListing([new SchemaObject("scratch_only", SchemaObjectType.Table, "scratch_only", 3, null)], false));

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-tables-row").Select(r => r.GetAttribute("data-name")).Should().Equal("scratch_only"));
    }

    [Fact]
    public void An_empty_file_shows_the_empty_state()
    {
        Operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>()).Returns(new SchemaListing([], false));

        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Tables>();

        cut.WaitForElement(".sqlite-tables-empty");
        cut.Find(".sqlite-tables-empty").TextContent.Should().Contain("No tables in").And.Contain("reports.db");
    }

    [Fact]
    public void A_listing_failure_shows_the_friendly_error()
    {
        Operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<SchemaListing>>(_ => throw new SqliteException("file is not a database", 26));

        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Tables>();

        cut.WaitForElement(".sqlite-tables-error");
        cut.Find(".sqlite-tables-error").TextContent.Should().Contain("The file is not a valid or healthy SQLite database.");
    }

    // A stand-in for a running COUNT(*): only completes when told to, or when its token is cancelled.
    private static async Task<long> HeldCountAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    private void SetupScratchOnly()
    {
        Operations.GetTableDetailAsync(SecretB, "scratch_only", Arg.Any<CancellationToken>()).Returns(new TableDetail(
            "scratch_only", SchemaObjectType.Table, "CREATE TABLE scratch_only(id INTEGER)", false, null,
            [new ColumnInfo("id", "INTEGER", true, null, 0, false, false)], [], []));
        Operations.SampleColumnsAsync(SecretB, "scratch_only", ColumnSample.DefaultSampleRows, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ColumnSample>());
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderWithPicker() =>
        RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SbConsole.Plugins.Sqlite.Pages.Tables>(0);
            builder.CloseComponent();
        });

    private static async Task SelectSecondAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        cut.WaitForAssertion(() => cut.FindAll(".sqlite-connection-option").Should().HaveCount(2));
        _ = cut.FindAll(".sqlite-connection-option")[1].ClickAsync(new());
    }

    [Fact]
    public void Count_exactly_can_be_cancelled_and_offered_again()
    {
        Operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(ci => HeldCountAsync(ci.ArgAt<CancellationToken>(2)));
        var cut = RenderPage();

        cut.Find(".sqlite-table-count").Click();
        cut.WaitForElement(".sqlite-table-count-cancel").Click();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-table-count").Should().HaveCount(1));
        cut.Find(".sqlite-table-rows").TextContent.Should().Be("41.2M rows ≈");
        cut.FindAll(".sqlite-table-count-cancel").Should().BeEmpty();
        cut.FindAll(".sqlite-table-count-error").Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_count_on_the_previous_table_does_not_break_the_new_tables_cancel()
    {
        var staleCount = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        Operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(_ => staleCount.Task); // ignores its token
        Operations.CountRowsAsync(Secret, "sessions", Arg.Any<CancellationToken>()).Returns(ci => HeldCountAsync(ci.ArgAt<CancellationToken>(2)));
        var cut = RenderPage();
        cut.Find(".sqlite-table-count").Click();
        cut.WaitForElement(".sqlite-table-count-cancel");

        cut.Find("[data-name='sessions']").Click();
        cut.WaitForAssertion(() => cut.Find(".sqlite-table-name").TextContent.Should().Be("sessions"));
        cut.Find(".sqlite-table-count").Click();
        cut.WaitForElement(".sqlite-table-count-cancel");

        staleCount.SetResult(41_204_117L); // events' count finishes late, after sessions' count started
        await Task.Delay(100); // no observable signal for the stale continuation; it only mutates private state
        cut.Find(".sqlite-table-count-cancel").Click();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-table-count").Should().HaveCount(1));
        cut.Find(".sqlite-table-rows").TextContent.Should().NotContain("41,204,117");
    }

    [Fact]
    public void A_count_failure_shows_the_count_error()
    {
        Operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>())
            .Returns<Task<long>>(_ => throw new SqliteException("file is not a database", 26));
        var cut = RenderPage();

        cut.Find(".sqlite-table-count").Click();

        cut.WaitForElement(".sqlite-table-count-error").TextContent.Should().Contain("The file is not a valid or healthy SQLite database.");
        cut.FindAll(".sqlite-table-count").Should().HaveCount(1);
    }

    [Fact]
    public void A_failed_export_shows_the_friendly_message_and_does_not_download()
    {
        Operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>())
            .Returns<Task<CsvExport>>(_ => throw new SqliteException("file is not a database", 26));
        var cut = RenderPage();

        cut.Find(".sqlite-table-export").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-export-message").TextContent.Should().Contain("The file is not a valid or healthy SQLite database."));
        JSInterop.Invocations.Where(i => i.Identifier == "downloadFromStream").Should().BeEmpty();
        cut.Find(".sqlite-table-export").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void A_js_failure_shows_the_browser_message()
    {
        Operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>())
            .Returns(new CsvExport("events.csv", Encoding.UTF8.GetBytes("id\r\n1\r\n"), 1, false));
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Sqlite/sqlite.js");
        module.SetupVoid("downloadFromStream", _ => true).SetException(new Microsoft.JSInterop.JSException("boom"));
        var cut = RenderPage();

        cut.Find(".sqlite-table-export").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-export-message").TextContent.Should().Be("The browser couldn't save the file."));
    }

    [Fact]
    public async Task A_stale_export_neither_downloads_nor_writes_a_message_after_a_table_switch()
    {
        var release = new TaskCompletionSource<CsvExport>(TaskCreationOptions.RunContinuationsAsynchronously);
        Operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Sqlite/sqlite.js");
        module.SetupVoid("downloadFromStream", _ => true).SetVoidResult();
        var cut = RenderPage();
        cut.Find(".sqlite-table-export").Click();

        cut.Find("[data-name='sessions']").Click();
        cut.WaitForAssertion(() => cut.Find(".sqlite-table-name").TextContent.Should().Be("sessions"));
        cut.Find(".sqlite-table-export").HasAttribute("disabled").Should().BeFalse("the export flag belongs to the table that started it");
        release.SetResult(new CsvExport("events.csv", Encoding.UTF8.GetBytes("id\r\n1\r\n"), 1, false));
        await Task.Delay(100);

        JSInterop.Invocations.Where(i => i.Identifier == "downloadFromStream").Should().BeEmpty();
        cut.FindAll(".sqlite-table-export-message").Should().BeEmpty();
    }

    [Fact]
    public async Task A_late_listing_for_the_previous_connection_never_shows_under_the_new_one()
    {
        TwoConnections();
        SetupScratchOnly();
        var lateA = new TaskCompletionSource<SchemaListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aContinued = new TaskCompletionSource();
        Operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var listing = await lateA.Task;
            aContinued.TrySetResult();
            return listing;
        });
        var releaseB = new TaskCompletionSource<SchemaListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        Operations.ListSchemaAsync(SecretB, Arg.Any<CancellationToken>()).Returns(_ => releaseB.Task);
        var cut = RenderWithPicker();
        cut.WaitForElement(".sqlite-page-busy");

        await SelectSecondAsync(cut);
        cut.WaitForAssertion(() => cut.Find(".sqlite-connection-menu").TextContent.Should().Contain("sqlite-scratch"));

        lateA.SetResult(Listing);
        await aContinued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100); // let the handler's continuation and the page's stale check run

        cut.FindAll(".sqlite-tables-row").Should().BeEmpty("A's tables must not show under B");
        cut.FindAll(".sqlite-page-busy").Should().HaveCount(1, "the busy indicator follows B's load, which is still running");

        releaseB.SetResult(new SchemaListing([new SchemaObject("scratch_only", SchemaObjectType.Table, "scratch_only", 3, null)], false));

        cut.WaitForAssertion(() => cut.Find(".sqlite-table-name").TextContent.Should().Be("scratch_only"));
        cut.FindAll(".sqlite-tables-row").Select(r => r.GetAttribute("data-name")).Should().Equal("scratch_only");
        cut.FindAll(".sqlite-page-busy").Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_connections_clears_the_selection_and_the_error()
    {
        TwoConnections();
        var releaseB = new TaskCompletionSource<SchemaListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        Operations.ListSchemaAsync(SecretB, Arg.Any<CancellationToken>()).Returns(_ => releaseB.Task);
        var cut = RenderWithPicker();
        cut.WaitForElement("[data-name='events']");
        cut.FindAll(".sql-is-selected").Should().HaveCount(1);

        await SelectSecondAsync(cut);

        cut.WaitForAssertion(() => cut.FindAll(".sql-is-selected").Should().BeEmpty());
        cut.FindAll(".sqlite-table-name").Should().BeEmpty();

        releaseB.SetException(new SqliteException("file is not a database", 26));
        cut.WaitForElement(".sqlite-tables-error");
        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        cut.WaitForAssertion(() => cut.FindAll(".sqlite-connection-option").Should().HaveCount(2));
        _ = cut.FindAll(".sqlite-connection-option")[0].ClickAsync(new());

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-tables-error").Should().BeEmpty());
        cut.WaitForElement("[data-name='events']");
    }

    [Fact]
    public void The_tabs_and_list_expose_accessibility_state()
    {
        var cut = RenderPage();

        cut.Find(".sqlite-tables-filter").GetAttribute("aria-label").Should().Be("Filter tables");
        cut.Find("[data-name='events']").GetAttribute("aria-current").Should().Be("true");
        cut.Find("[data-name='sessions']").HasAttribute("aria-current").Should().BeFalse();
        cut.Find(".sql-tabs").GetAttribute("role").Should().Be("tablist");
        cut.Find(".sqlite-tab-columns").GetAttribute("role").Should().Be("tab");
        cut.Find(".sqlite-tab-columns").GetAttribute("aria-selected").Should().Be("true");
        cut.Find(".sqlite-tab-ddl").GetAttribute("aria-selected").Should().Be("false");
    }
}
