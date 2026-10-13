using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

// Page type fully qualified for the same namespace-collision reason as OverviewPageTests
// (SbConsole.Plugins.Sqlite.Query is also the handlers' namespace).
public class QueryPageTests : SqlitePageTestBase
{
    private const string Sql = "SELECT t.name AS tenant, COUNT(*) AS n, MIN(e.ts) AS oldest, payload FROM events e";
    private const string WriteSql = "DELETE FROM events WHERE tenant_id IN (SELECT id FROM tenants WHERE status = 'churned');";
    private const string SecretB = "Path=/data/scratch.db";
    private readonly Guid _secondId = Guid.NewGuid();

    private static CellValue Text(string value) => new(CellKind.Text, value);

    private static CellValue Int(long value) => new(CellKind.Integer, value.ToString(System.Globalization.CultureInfo.InvariantCulture), value);

    private static readonly QueryResult Tenants = new(
        [
            new QueryColumn("tenant", "TEXT", false),
            new QueryColumn("n", null, false),
            new QueryColumn("oldest", "INTEGER", true),
            new QueryColumn("payload", "BLOB", false),
        ],
        [
            [Text("acme-uk"), Int(4802114), Int(1693526400000), new CellValue(CellKind.Blob, "BLOB · 16 bytes")],
            [Text("northwind"), Int(3118920), Int(1696118400000), new CellValue(CellKind.Null, "NULL")],
        ],
        TotalRows: 412, TotalCapped: false, StatementCount: 1, ResultStatement: 1, Duration: TimeSpan.FromMilliseconds(2840));

    private static readonly WriteScriptResult DryRun = new(false,
        [new WriteStatementResult("DELETE", WriteSql, 6_118_402, 13_087_220)], TimeSpan.FromSeconds(41.2), null);

    private static readonly WriteScriptResult Committed = new(true,
        [new WriteStatementResult("DELETE", WriteSql, 6_118_402, 13_087_220)], TimeSpan.FromSeconds(41.2),
        new WriteFileState(1_181_116_006, 18.4));

    public QueryPageTests()
    {
        Operations.RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>()).Returns(Tenants);
        Operations.DryRunWriteAsync(Secret, WriteSql, Arg.Any<CancellationToken>()).Returns(DryRun);
        Operations.RunWriteAsync(Secret, WriteSql, Arg.Any<CancellationToken>()).Returns(Committed);
    }

    private IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Query> RenderPage()
    {
        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Query>();
        cut.WaitForElement(".sqlite-query-editor");
        cut.WaitForAssertion(() => cut.Find(".sqlite-query-meta").TextContent.Should().Contain("statement timeout"));
        return cut;
    }

    private IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Query> RenderAndRun(string sql = Sql)
    {
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input(sql);
        cut.Find(".sqlite-query-run").Click();
        cut.WaitForElement(".sqlite-query-grid");
        return cut;
    }

    private void UseConnection(bool readWrite, bool prod)
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", prod ? ["prod"] : [],
                Summary: new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = readWrite ? "read-write" : "read-only" }),
        ]);
    }

    private void TwoConnections()
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", [], Summary: new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-write" }),
            new ConnectionInfo(_secondId, "sqlite-scratch", "sqlite", [], Summary: new Dictionary<string, string> { ["File"] = "scratch.db", ["Mode"] = "read-write" }),
        ]);
        Connections.GetSecretAsync(_secondId, Arg.Any<CancellationToken>()).Returns(SecretB);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderWithPicker()
    {
        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<SbConsole.Plugins.Sqlite.Pages.Query>(0);
            builder.CloseComponent();
        });
        cut.WaitForElement(".sqlite-query-editor");
        return cut;
    }

    private static async Task SelectSecondAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await cut.Find(".sqlite-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        cut.WaitForAssertion(() => cut.FindAll(".sqlite-connection-option").Should().HaveCount(2));
        _ = cut.FindAll(".sqlite-connection-option")[1].ClickAsync(new());
    }

    private IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Query> RenderInWriteMode(bool prod = false)
    {
        UseConnection(readWrite: true, prod: prod);
        var cut = RenderPage();
        cut.Find(".sqlite-query-mode-write").Click();
        cut.WaitForElement(".sqlite-query-write-banner");
        cut.Find(".sqlite-query-editor").Input(WriteSql);
        return cut;
    }

    // ---- read-only query ----

    [Fact]
    public void The_sql_query_parameter_prefills_the_editor_and_the_meta_names_the_read_only_open()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/p/sqlite/query?sql=" + Uri.EscapeDataString("SELECT * FROM events"));

        var cut = RenderPage();

        cut.Find(".sqlite-query-editor").GetAttribute("value").Should().Be("SELECT * FROM events");
        cut.Find(".sqlite-query-meta").TextContent.Should().Be("opened with mode=ro · statement timeout 30 s");
        cut.Find(".sqlite-query-mode-read").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void Run_renders_the_grid_with_column_types_null_blob_and_the_date_hint()
    {
        var cut = RenderAndRun();

        cut.FindAll(".sqlite-query-col").Select(h => h.GetAttribute("data-column")).Should().Equal("tenant", "n", "oldest", "payload");
        cut.Find("[data-column='tenant'] .sqlite-query-col-type").TextContent.Should().Be("TEXT");
        cut.FindAll("[data-column='n'] .sqlite-query-col-type").Should().BeEmpty("an expression has no declared type");
        var rows = cut.FindAll(".sqlite-query-grid tbody tr");
        rows.Should().HaveCount(2);
        rows[0].QuerySelectorAll("td")[1].TextContent.Should().Be("acme-uk");
        rows[0].QuerySelector(".sql-cell-blob")!.TextContent.Trim().Should().Be("BLOB · 16 bytes");
        rows[1].QuerySelector(".sql-cell-null")!.TextContent.Trim().Should().Be("NULL");
        rows[0].QuerySelector(".sql-hint")!.TextContent.Should().Be("2023-09-01");
        rows[0].QuerySelectorAll(".sql-hint").Should().HaveCount(1, "only the hinted column gets a date");
        cut.Find(".sqlite-query-status").TextContent.Should().Contain("412 rows").And.Contain("2.8 s");
        cut.Find(".sqlite-query-count").TextContent.Should().Be("2 of 412 rows");
    }

    [Fact]
    public async Task Ctrl_or_cmd_enter_runs_the_query()
    {
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input(Sql);

        await cut.Find(".sqlite-query-editor").KeyDownAsync(new KeyboardEventArgs { Key = "Enter", MetaKey = true });

        cut.WaitForElement(".sqlite-query-grid");
        await Operations.Received(1).RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Plain_enter_does_not_run()
    {
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input(Sql);

        await cut.Find(".sqlite-query-editor").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        await Operations.DidNotReceive().RunQueryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Load_100_more_re_runs_the_same_sql_with_a_larger_limit()
    {
        Operations.RunQueryAsync(Secret, Sql, 200, Arg.Any<CancellationToken>()).Returns(Tenants with
        {
            Rows = [.. Tenants.Rows, [Text("globex"), Int(2204311), Int(1704067200000), new CellValue(CellKind.Null, "NULL")]],
        });
        var cut = RenderAndRun();
        cut.Find(".sqlite-query-editor").Input("SELECT 'edited after the run'");

        cut.Find(".sqlite-query-more").Click();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-query-grid tbody tr").Should().HaveCount(3));
        await Operations.Received(1).RunQueryAsync(Secret, Sql, 200, Arg.Any<CancellationToken>());
        cut.Find(".sqlite-query-count").TextContent.Should().Be("3 of 412 rows");
    }

    [Fact]
    public void Load_more_is_hidden_when_every_row_is_shown()
    {
        Operations.RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>()).Returns(Tenants with { TotalRows = 2 });

        var cut = RenderAndRun();

        cut.FindAll(".sqlite-query-more").Should().BeEmpty();
    }

    [Fact]
    public void A_capped_count_reads_100000_plus()
    {
        Operations.RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>())
            .Returns(Tenants with { TotalRows = QueryResult.CountCap, TotalCapped = true });

        var cut = RenderAndRun();

        cut.Find(".sqlite-query-status").TextContent.Should().Contain("100,000+ rows");
        cut.Find(".sqlite-query-count").TextContent.Should().Be("2 of 100,000+ rows");
    }

    [Fact]
    public void A_script_without_a_result_set_says_so()
    {
        Operations.RunQueryAsync(Secret, "PRAGMA optimize", QueryResult.DefaultLimit, Arg.Any<CancellationToken>())
            .Returns(new QueryResult([], [], 0, false, 1, null, TimeSpan.FromMilliseconds(3)));
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input("PRAGMA optimize");

        cut.Find(".sqlite-query-run").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-query-status").TextContent.Should().Contain("no statement returned rows"));
        cut.FindAll(".sqlite-query-grid").Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_run_shows_a_banner_with_retry_and_no_settings_link_for_a_sql_error()
    {
        Operations.RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>())
            .Returns<Task<QueryResult>>(_ => throw new SqliteScriptRefusedException("ATTACH isn't allowed here."), _ => Task.FromResult(Tenants));
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input(Sql);

        cut.Find(".sqlite-query-run").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-query-error-text").TextContent.Should().Be("ATTACH isn't allowed here."));
        cut.FindAll(".sqlite-query-error .sqlite-query-settings-link").Should().BeEmpty();

        cut.Find(".sqlite-query-retry").Click();

        cut.WaitForElement(".sqlite-query-grid");
        cut.FindAll(".sqlite-query-error").Should().BeEmpty();
        await Operations.Received(2).RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_missing_file_error_links_to_connection_settings()
    {
        Operations.RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>())
            .Returns<Task<QueryResult>>(_ => throw new SqliteException("unable to open database file", 14));
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input(Sql);

        cut.Find(".sqlite-query-run").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-query-error-text").TextContent.Should().Be("File not found, or not readable by the SbConsole container user."));
        cut.Find(".sqlite-query-error .sqlite-query-settings-link").GetAttribute("href").Should().Be("/connections");
    }

    [Fact]
    public void Explain_renders_the_plan_tree_and_tints_scans()
    {
        Operations.ExplainQueryAsync(Secret, Sql, Arg.Any<CancellationToken>()).Returns(new ExplainPlan(Sql, 2,
        [
            new ExplainNode(2, 0, "SEARCH e USING INDEX idx_events_ts (ts<?)"),
            new ExplainNode(5, 0, "SCAN t"),
            new ExplainNode(7, 5, "USE TEMP B-TREE FOR GROUP BY"),
        ]));
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input(Sql);

        cut.Find(".sqlite-query-explain").Click();

        cut.WaitForElement(".sqlite-query-plan");
        var nodes = cut.FindAll(".sqlite-query-plan-node");
        nodes.Select(n => n.TextContent.Trim()).Should().Equal("SEARCH e USING INDEX idx_events_ts (ts<?)", "SCAN t", "USE TEMP B-TREE FOR GROUP BY");
        nodes.Select(n => n.GetAttribute("data-depth")).Should().Equal("0", "0", "1");
        nodes[1].ClassList.Should().Contain("sql-plan-scan");
        nodes[0].ClassList.Should().NotContain("sql-plan-scan");
        cut.Find(".sqlite-query-plan").TextContent.Should().Contain("statement 1 of 2");
    }

    [Fact]
    public void Json_view_is_an_array_of_objects()
    {
        var cut = RenderAndRun();

        cut.Find(".sqlite-query-tab-json").Click();

        var json = cut.Find(".sqlite-query-json").TextContent;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement[0].GetProperty("tenant").GetString().Should().Be("acme-uk");
        doc.RootElement[0].GetProperty("n").GetInt64().Should().Be(4802114);
        doc.RootElement[0].GetProperty("payload").GetString().Should().Be("BLOB · 16 bytes");
        doc.RootElement[1].GetProperty("payload").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void Chart_tab_shows_a_bar_chart_of_the_first_text_column_against_the_first_numeric_one()
    {
        var cut = RenderAndRun();

        cut.Find(".sqlite-query-tab-chart").Click();

        cut.Find(".sqlite-query-chart").TextContent.Should().Contain("tenant").And.Contain("n");
        cut.FindAll(".sqlite-query-chart .mud-chart").Should().NotBeEmpty();
    }

    [Fact]
    public void Chart_tab_is_hidden_without_a_text_and_numeric_pair()
    {
        Operations.RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>()).Returns(new QueryResult(
            [new QueryColumn("n", null, false)], [[Int(1)]], 1, false, 1, 1, TimeSpan.FromMilliseconds(1)));

        var cut = RenderAndRun();

        cut.FindAll(".sqlite-query-tab-grid").Should().HaveCount(1);
        cut.FindAll(".sqlite-query-tab-chart").Should().BeEmpty();
    }

    [Fact]
    public async Task Export_csv_downloads_the_result_sql_and_is_audited()
    {
        Operations.ExportQueryCsvAsync(Secret, Sql, CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>())
            .Returns(new CsvExport("query.csv", Encoding.UTF8.GetBytes("tenant\r\nacme-uk\r\n"), 1, false));
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Sqlite/sqlite.js");
        module.SetupVoid("downloadFromStream", _ => true).SetVoidResult();
        var cut = RenderAndRun();

        cut.Find(".sqlite-query-export").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-query-export-message").TextContent.Should().Be("Exported 1 rows to query.csv."));
        module.VerifyInvoke("downloadFromStream").Arguments[0].Should().Be("query.csv");
        await Audit.Received(1).RecordAsync("sqlite.query.export", ConnectionName, ActionRisk.Safe, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- write mode ----

    [Fact]
    public void Write_is_disabled_with_the_reason_on_a_read_only_connection()
    {
        var cut = RenderPage();

        cut.Find(".sqlite-query-mode-write").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".sqlite-query-mode-write-wrap").GetAttribute("title")
            .Should().Be("This connection is read-only — set Default mode to Read-write in Connections.");
    }

    [Fact]
    public async Task Enabling_write_on_prod_asks_for_the_name_and_declining_stays_read_only()
    {
        UseConnection(readWrite: true, prod: true);
        Confirmation.ConfirmAsync("Enable write mode on", ConnectionName, true, null, Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        cut.Find(".sqlite-query-mode-write").Click();

        await Confirmation.Received(1).ConfirmAsync("Enable write mode on", ConnectionName, true, null, Arg.Any<CancellationToken>());
        cut.FindAll(".sqlite-query-write-banner").Should().BeEmpty();
        cut.Find(".sqlite-query-mode-read").GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll(".sqlite-query-run").Should().HaveCount(1);
    }

    [Fact]
    public async Task Enabling_write_on_prod_when_confirmed_tints_the_header_and_shows_the_banner()
    {
        UseConnection(readWrite: true, prod: true);
        var cut = RenderPage();

        cut.Find(".sqlite-query-mode-write").Click();

        cut.WaitForElement(".sqlite-query-write-banner");
        await Confirmation.Received(1).ConfirmAsync("Enable write mode on", ConnectionName, true, null, Arg.Any<CancellationToken>());
        cut.Find(".sql-header").ClassList.Should().Contain("sql-header-write");
        cut.Find(".sqlite-query-meta").TextContent.Should().Be("write mode on a prod file · every statement runs inside a transaction and is audited");
        cut.FindAll(".sqlite-query-run").Should().BeEmpty("write mode never runs a bare statement");
        cut.FindAll(".sqlite-query-dryrun").Should().HaveCount(1);
    }

    [Fact]
    public async Task Enabling_write_on_a_non_prod_connection_needs_no_confirmation()
    {
        var cut = RenderInWriteMode();

        cut.Find(".sqlite-query-meta").TextContent.Should().Be("write mode · every statement runs inside a transaction and is audited");
        await Confirmation.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Run_in_transaction_needs_a_dry_run_of_the_same_text()
    {
        var cut = RenderInWriteMode();
        cut.Find(".sqlite-query-runwrite").HasAttribute("disabled").Should().BeTrue();

        cut.Find(".sqlite-query-dryrun").Click();

        cut.WaitForElement(".sqlite-query-dryrun-panel");
        cut.Find(".sqlite-query-runwrite").HasAttribute("disabled").Should().BeFalse();

        cut.Find(".sqlite-query-editor").Input(WriteSql + " ");

        cut.Find(".sqlite-query-runwrite").HasAttribute("disabled").Should().BeTrue("the text changed since the dry run");
        cut.Find(".sqlite-query-dryrun-stale").TextContent.Should().Contain("changed since the dry run");

        cut.Find(".sqlite-query-editor").Input(WriteSql);

        cut.Find(".sqlite-query-runwrite").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void The_dry_run_panel_reports_direct_and_cascaded_rows_and_the_cost()
    {
        var cut = RenderInWriteMode();

        cut.Find(".sqlite-query-dryrun").Click();

        var panel = cut.WaitForElement(".sqlite-query-dryrun-panel");
        cut.FindAll(".sqlite-query-dryrun-statement").Should().ContainSingle()
            .Which.TextContent.Should().Contain("DELETE").And.Contain("6,118,402 rows");
        cut.Find(".sqlite-query-dryrun-cascaded").TextContent.Should().Contain("13,087,220");
        cut.Find(".sqlite-query-dryrun-duration").TextContent.Should().Contain("41.2 s");
        panel.TextContent.Should().Contain("freed pages stay in the file until VACUUM").And.Contain("rolled back");
        Audit.ReceivedCalls().Should().BeEmpty("a dry run is a query and writes no audit row");
    }

    [Fact]
    public async Task Run_in_transaction_on_prod_confirms_with_the_dry_run_count_and_declining_runs_nothing()
    {
        var cut = RenderInWriteMode(prod: true);
        cut.Find(".sqlite-query-dryrun").Click();
        cut.WaitForElement(".sqlite-query-dryrun-panel");
        Confirmation.ConfirmAsync("Run write script on", ConnectionName, true, 19_205_622, Arg.Any<CancellationToken>()).Returns(false);

        cut.Find(".sqlite-query-runwrite").Click();

        await Confirmation.Received(1).ConfirmAsync("Run write script on", ConnectionName, true, 19_205_622, Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().RunWriteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        cut.FindAll(".sqlite-query-write-result").Should().BeEmpty();
    }

    [Fact]
    public async Task A_committed_write_shows_the_result_panel_and_checkpoint_runs()
    {
        Operations.CheckpointWalAsync(Secret, Arg.Any<CancellationToken>()).Returns(new WalCheckpointResult(false, 2048, 2048));
        var cut = RenderInWriteMode(prod: true);
        cut.Find(".sqlite-query-dryrun").Click();
        cut.WaitForElement(".sqlite-query-dryrun-panel");

        cut.Find(".sqlite-query-runwrite").Click();

        var result = cut.WaitForElement(".sqlite-query-write-result");
        await Operations.Received(1).RunWriteAsync(Secret, WriteSql, Arg.Any<CancellationToken>());
        result.TextContent.Should().Contain("committed");
        cut.Find(".sqlite-query-write-statement").TextContent.Should().Contain("DELETE").And.Contain("6,118,402");
        result.TextContent.Should().Contain("13,087,220").And.Contain("41.2 s");
        cut.Find(".sqlite-query-write-after").TextContent.Should().Contain("-wal 1.1 GB").And.Contain("18% free pages");
        cut.Find(".sqlite-query-write-audit").TextContent.Should().Contain("sqlite.query.write").And.Contain(QueryAudit.Hash(WriteSql));
        await Audit.Received(1).RecordAsync("sqlite.query.write", ConnectionName, ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        cut.Find(".sqlite-query-runwrite").HasAttribute("disabled").Should().BeTrue("a committed script needs a fresh dry run before it runs again");

        cut.Find(".sqlite-query-checkpoint").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-query-checkpoint-result").TextContent.Should().Contain("2,048 frames").And.Contain("2,048 checkpointed"));
        await Audit.Received(1).RecordAsync("sqlite.wal.checkpoint", ConnectionName, ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_failed_write_shows_the_banner_and_keeps_nothing()
    {
        Operations.RunWriteAsync(Secret, WriteSql, Arg.Any<CancellationToken>())
            .Returns<Task<WriteScriptResult>>(_ => throw new SqliteException("database is locked", 5));
        var cut = RenderInWriteMode();
        cut.Find(".sqlite-query-dryrun").Click();
        cut.WaitForElement(".sqlite-query-dryrun-panel");

        cut.Find(".sqlite-query-runwrite").Click();

        cut.WaitForAssertion(() => cut.Find(".sqlite-query-error-text").TextContent.Should().StartWith("Database is locked"));
        cut.FindAll(".sqlite-query-write-result").Should().BeEmpty();
    }

    [Fact]
    public void Back_to_read_only_leaves_write_mode()
    {
        var cut = RenderInWriteMode();
        cut.Find(".sqlite-query-dryrun").Click();
        cut.WaitForElement(".sqlite-query-dryrun-panel");
        cut.Find(".sqlite-query-runwrite").Click();
        cut.WaitForElement(".sqlite-query-write-result");

        cut.Find(".sqlite-query-back-readonly").Click();

        cut.FindAll(".sqlite-query-write-banner").Should().BeEmpty();
        cut.FindAll(".sqlite-query-write-result").Should().BeEmpty();
        cut.Find(".sqlite-query-mode-read").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find(".sqlite-query-meta").TextContent.Should().StartWith("opened with mode=ro");
    }

    [Fact]
    public async Task Switching_connection_resets_to_read_only_and_drops_a_late_result_for_the_old_one()
    {
        TwoConnections();
        var lateA = new TaskCompletionSource<QueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Operations.RunQueryAsync(Secret, Sql, QueryResult.DefaultLimit, Arg.Any<CancellationToken>()).Returns(_ => lateA.Task);
        var cut = RenderWithPicker();
        cut.Find(".sqlite-query-mode-write").Click();
        cut.WaitForElement(".sqlite-query-write-banner");
        cut.Find(".sqlite-query-mode-read").Click();
        cut.Find(".sqlite-query-editor").Input(Sql);
        cut.Find(".sqlite-query-run").Click();
        cut.Find(".sqlite-query-mode-write").Click(); // back into write mode on A before switching
        cut.WaitForElement(".sqlite-query-write-banner");

        await SelectSecondAsync(cut);
        cut.WaitForAssertion(() => cut.Find(".sqlite-connection-menu").TextContent.Should().Contain("sqlite-scratch"));
        cut.WaitForAssertion(() => cut.FindAll(".sqlite-query-write-banner").Should().BeEmpty());

        lateA.SetResult(Tenants);
        await Task.Delay(100); // let the handler's continuation and the page's stale check run
        cut.Render();

        cut.FindAll(".sqlite-query-grid").Should().BeEmpty("A's result must not show under B");
        cut.Find(".sqlite-query-mode-read").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find(".sqlite-query-editor").GetAttribute("value").Should().Be(Sql, "the SQL is portable and stays");
    }

    // ---- saved queries ----

    [Fact]
    public async Task Saved_queries_load_on_click()
    {
        await SavedQueriesStore.WriteAsync(Store,
        [
            new SavedQuery(Guid.NewGuid(), "events older than 90d", "SELECT * FROM events WHERE ts < 1", Time.GetUtcNow()),
            new SavedQuery(Guid.NewGuid(), "orphan sessions", "SELECT * FROM sessions", Time.GetUtcNow()),
        ]);
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.FindAll(".sqlite-query-saved-row").Should().HaveCount(2));

        cut.Find("[data-name='orphan sessions']").Click();

        cut.Find(".sqlite-query-editor").GetAttribute("value").Should().Be("SELECT * FROM sessions");
    }

    [Fact]
    public async Task Save_prompts_for_a_name_and_stores_the_query()
    {
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input("SELECT 42");

        cut.Find(".sqlite-query-save").Click();
        cut.Find(".sqlite-query-save-name input").Input("the answer");
        cut.Find(".sqlite-query-save-confirm").Click();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-query-saved-row").Select(r => r.GetAttribute("data-name")).Should().Equal("the answer"));
        cut.FindAll(".sqlite-query-save-name").Should().BeEmpty();
        (await SavedQueriesStore.ReadAsync(Store)).Should().ContainSingle().Which.Sql.Should().Be("SELECT 42");
    }

    [Fact]
    public void Save_can_be_cancelled()
    {
        var cut = RenderPage();
        cut.Find(".sqlite-query-editor").Input("SELECT 42");

        cut.Find(".sqlite-query-save").Click();
        cut.Find(".sqlite-query-save-cancel").Click();

        cut.FindAll(".sqlite-query-save-name").Should().BeEmpty();
        Store.Values.Should().NotContainKey(SavedQueriesStore.Key);
    }

    [Fact]
    public async Task Delete_removes_a_saved_query()
    {
        var id = Guid.NewGuid();
        await SavedQueriesStore.WriteAsync(Store, [new SavedQuery(id, "orphan sessions", "SELECT * FROM sessions", Time.GetUtcNow())]);
        var cut = RenderPage();
        cut.WaitForElement(".sqlite-query-saved-row");

        cut.Find(".sqlite-query-saved-delete").Click();

        cut.WaitForAssertion(() => cut.FindAll(".sqlite-query-saved-row").Should().BeEmpty());
        (await SavedQueriesStore.ReadAsync(Store)).Should().BeEmpty();
    }
}
