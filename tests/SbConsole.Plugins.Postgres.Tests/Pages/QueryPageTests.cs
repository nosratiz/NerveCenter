using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

public class QueryPageTests : PgPageTestBase
{
    private static PgCell Text(string value) => new(PgCellKind.Text, value);

    private static PgQueryResult Result(int rows = 3, bool write = false) => new(
        [new PgResultColumn("country", "text"), new PgResultColumn("n", "int8"), new PgResultColumn("meta", "jsonb")],
        Enumerable.Range(1, rows).Select(i => (IReadOnlyList<PgCell>)
            [Text("DE" + i), Text(i.ToString()), i == 2 ? new PgCell(PgCellKind.Null, null) : new PgCell(PgCellKind.Json, "{…}", 1, "{ \"a\": 1 }")]).ToList(),
        false, write ? 4 : null, TimeSpan.FromSeconds(1.84), 1, write ? "UPDATE 4" : "SELECT 3");

    public QueryPageTests()
    {
        Operations.ListDatabasesAsync(Secret, Arg.Any<CancellationToken>()).Returns(["app"]);
        Operations.RunQueryAsync(Secret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => Result(write: c.Arg<PgQueryRequest>().Write));
    }

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderQuery(string? query = null)
    {
        Nav.NavigateTo($"/p/postgres/query?connectionId={ConnectionId}{query}");
        var cut = RenderWithPopovers(b =>
        {
            b.OpenComponent<SbConsole.Plugins.Postgres.Pages.Query>(0);
            b.CloseComponent();
        });
        cut.WaitForElement(".pg-editor");
        return cut;
    }

    private static void Type(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string sql) =>
        cut.Find(".pg-editor textarea").Input(sql);

    private static void Run(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string sql = "select 1")
    {
        Type(cut, sql);
        cut.WaitForState(() => !cut.Find(".pg-run").HasAttribute("disabled"));
        cut.Find(".pg-run").Click();
    }

    private void SetMode(string mode)
    {
        Connections.ListAsync("postgres", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "postgres", ["prod"], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["Server"] = "db:5432", ["Database"] = "app", ["Mode"] = mode }),
        ]);
    }

    private static void ClickWrite(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindAll(".pg-mode-toggle .mud-toggle-item").First(i => i.TextContent.Contains("Write")).Click();

    [Fact]
    public void Read_run_renders_grid_with_type_names_and_status()
    {
        var cut = RenderQuery();
        Run(cut);

        cut.WaitForElement(".pg-result-grid");
        var headers = cut.FindAll(".pg-result-grid th").Select(h => h.TextContent).ToList();
        headers.Should().Contain(h => h.Contains("country") && h.Contains("text"));
        headers.Should().Contain(h => h.Contains("n") && h.Contains("int8"));
        cut.FindAll(".pg-result-grid tbody tr").Should().HaveCount(3);
        cut.FindAll(".pg-result-grid .pg-null").Should().HaveCount(1);
        var status = cut.Find(".pg-status").TextContent;
        status.Should().Contain("3 rows").And.Contain("1.84 s").And.Contain("read-only txn")
            .And.Contain("timeout 30 s").And.Contain("cap 1,000").And.Contain("audited as pg.query.read");
        Operations.Received(1).RunQueryAsync(Secret, "app",
            Arg.Is<PgQueryRequest>(r => r.Sql == "select 1" && !r.Write && r.RowCap == 1000 && r.TimeoutS == 30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Show_all_reveals_rows_beyond_the_first_two_hundred()
    {
        Operations.RunQueryAsync(Secret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Result(250));
        var cut = RenderQuery();
        Run(cut);

        cut.WaitForElement(".pg-show-all");
        cut.FindAll(".pg-result-grid tbody tr").Should().HaveCount(200);
        cut.Markup.Should().Contain("200 of 250 rows shown");
        cut.Find(".pg-show-all").Click();
        cut.FindAll(".pg-result-grid tbody tr").Should().HaveCount(250);
        cut.FindAll(".pg-show-all").Should().BeEmpty();
    }

    [Fact]
    public void Json_tab_shows_rows_as_an_array_of_objects()
    {
        var cut = RenderQuery();
        Run(cut);
        cut.WaitForElement(".pg-result-grid");

        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("JSON")).Click();

        var json = cut.WaitForElement(".pg-result-json").TextContent;
        json.Should().Contain("\"country\": \"DE1\"").And.Contain("\"meta\": null");
    }

    [Fact]
    public void Explain_tab_loads_the_plan_when_activated()
    {
        Operations.ExplainAsync(Secret, "app", "select 1", 30, Arg.Any<CancellationToken>()).Returns("Result  (cost=0.00..0.01 rows=1 width=4)");
        var cut = RenderQuery();
        Run(cut);
        cut.WaitForElement(".pg-result-grid");

        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("EXPLAIN")).Click();

        cut.WaitForElement(".pg-explain").TextContent.Should().Contain("Result  (cost=0.00..0.01");
        cut.Markup.Should().Contain("EXPLAIN without ANALYZE");
    }

    [Fact]
    public void Write_toggle_is_disabled_when_the_connection_is_read_only()
    {
        SetMode("read-only");
        var cut = RenderQuery();

        cut.FindAll(".pg-mode-toggle .mud-toggle-item").First(i => i.TextContent.Contains("Write")).HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Connection is read-only");
    }

    [Fact]
    public void Entering_write_mode_on_prod_asks_for_confirmation_and_cancel_stays_read_only()
    {
        Confirmation.ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderQuery();

        ClickWrite(cut);

        cut.WaitForAssertion(() => Confirmation.Received(1).ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()));
        cut.FindAll(".pg-write-banner").Should().BeEmpty();
        Run(cut);
        cut.WaitForElement(".pg-status");
        Operations.Received().RunQueryAsync(Secret, "app", Arg.Is<PgQueryRequest>(r => !r.Write), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Confirmed_write_mode_shows_the_banner_and_back_to_read_only_clears_it()
    {
        Confirmation.ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderQuery();

        ClickWrite(cut);

        cut.WaitForElement(".pg-write-banner").TextContent.Should().Contain("Write mode on a PROD connection. Statements run in a transaction and are audited with your name.");
        Run(cut, "update t set a = 1");
        cut.WaitForElement(".pg-status").TextContent.Should().Contain("4 rows affected").And.Contain("committed").And.Contain("audited as pg.query.write");
        Operations.Received().RunQueryAsync(Secret, "app", Arg.Is<PgQueryRequest>(r => r.Write), Arg.Any<CancellationToken>());
        cut.Find(".pg-query-export").HasAttribute("disabled").Should().BeTrue();

        cut.Find(".pg-back-to-readonly").Click();

        cut.FindAll(".pg-write-banner").Should().BeEmpty();
    }

    [Fact]
    public void Query_error_shows_hint_line_rollback_sentence_and_marks_the_gutter()
    {
        Confirmation.ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        Operations.RunQueryAsync(Secret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns<PgQueryResult>(_ =>
            throw new Npgsql.PostgresException(messageText: "column \"custmer_id\" does not exist", severity: "ERROR", invariantSeverity: "ERROR",
                sqlState: "42703", hint: "Perhaps you meant to reference the column \"o.customer_id\".", position: 40));
        var cut = RenderQuery();
        ClickWrite(cut);
        cut.WaitForElement(".pg-write-banner");

        Run(cut, "UPDATE orders o\nSET status = 'x'\nWHERE o.custmer_id = 1");

        var error = cut.WaitForElement(".pg-query-error").TextContent;
        error.Should().Contain("Perhaps you meant").And.Contain("Line 3").And.Contain("Nothing was changed — the transaction rolled back.");
        cut.Find(".pg-query-error-meta").TextContent.Should().Contain("SQLSTATE 42703").And.Contain("audited as pg.query.write (failed)");
        cut.FindAll(".pg-gutter-error").Should().ContainSingle().Which.TextContent.Trim().Should().Be("3");
        cut.Find(".pg-fix-rerun").Should().NotBeNull();
    }

    [Fact]
    public void History_lists_prior_runs_and_clicking_one_loads_its_text()
    {
        var cut = RenderQuery();
        Run(cut, "select country, count(*) from customers group by country");
        cut.WaitForElement(".pg-result-grid");
        cut.WaitForAssertion(() => cut.FindAll(".pg-history-item").Should().HaveCount(1));
        cut.Find(".pg-history-item").TextContent.Should().Contain("select country").And.Contain("3 rows").And.Contain("ro");

        Type(cut, "select 2");
        cut.Find(".pg-history-item").Click();

        cut.Find(".pg-editor textarea").GetAttribute("value").Should().Be("select country, count(*) from customers group by country");
        cut.Markup.Should().Contain("Kept 90 days per connection.");
    }

    [Fact]
    public void Write_history_entries_link_to_the_audit_page()
    {
        Confirmation.ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderQuery();
        ClickWrite(cut);
        Run(cut, "update t set a = 1");

        cut.WaitForAssertion(() => cut.Find(".pg-history-item").TextContent.Should().Contain("write").And.Contain("admin"));
        var link = cut.Find(".pg-history-item a");
        link.GetAttribute("href").Should().Be("/audit");
        link.GetAttribute("title").Should().StartWith("audit sha256:");
    }

    [Fact]
    public void Sql_query_string_prefills_the_editor_once()
    {
        var cut = RenderQuery("&db=app&sql=" + Uri.EscapeDataString("SELECT * FROM public.orders LIMIT 100"));

        cut.Find(".pg-editor textarea").GetAttribute("value").Should().Be("SELECT * FROM public.orders LIMIT 100");
        cut.FindAll(".pg-gutter span").Should().HaveCount(1);
    }

    [Fact]
    public void Url_keeps_sql_so_a_reinitialised_page_still_prefills()
    {
        // Root cause of the lost prefill: the page's own URL write dropped sql, so the interactive instance (which re-reads
        // Navigation.Uri after prerender) found nothing to prefill from.
        var cut = RenderQuery("&db=app&sql=" + Uri.EscapeDataString("SELECT 1"));
        cut.WaitForAssertion(() => Nav.Uri.Should().Contain("db=app"));
        Nav.Uri.Should().Contain("sql=SELECT%201");

        cut.Find(".pg-editor textarea").GetAttribute("value").Should().Be("SELECT 1");
    }

    [Fact]
    public void Export_csv_calls_the_export_handler_with_the_editor_text()
    {
        Operations.ExportQueryCsvAsync(Secret, "app", "select 1", 30, Arg.Any<CancellationToken>())
            .Returns(new CsvExport("query.csv", [1, 2, 3], 3, false));
        var cut = RenderQuery();
        Type(cut, "select 1");
        cut.WaitForState(() => !cut.Find(".pg-query-export").HasAttribute("disabled"));

        cut.Find(".pg-query-export").Click();

        cut.WaitForAssertion(() => Operations.Received(1).ExportQueryCsvAsync(Secret, "app", "select 1", 30, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => Audit.Received().RecordAsync("pg.query.export", Arg.Any<string>(), ActionRisk.Safe, true, Arg.Any<string?>(), Arg.Any<CancellationToken>()));
    }

    // ---- fix round 1 -----------------------------------------------------------------------------------------

    private readonly Guid _otherId = Guid.NewGuid();
    private const string OtherSecret = "other-secret";
    private const string ModulePath = "./_content/SbConsole.Plugins.Postgres/postgres.js";

    private void AddSecondConnection()
    {
        Connections.ListAsync("postgres", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "postgres", ["prod"], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["Server"] = "db:5432", ["Database"] = "app", ["Mode"] = "read-write" }),
            new ConnectionInfo(_otherId, "pg-analytics", "postgres", [], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string> { ["Server"] = "an:5432", ["Database"] = "app", ["Mode"] = "read-write" }),
        ]);
        Connections.GetSecretAsync(_otherId, Arg.Any<CancellationToken>()).Returns(OtherSecret);
        Operations.ListDatabasesAsync(OtherSecret, Arg.Any<CancellationToken>()).Returns(["app"]);
        Operations.RunQueryAsync(OtherSecret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => Result(write: c.Arg<PgQueryRequest>().Write));
    }

    private static async Task SwitchToOtherAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await cut.Find(".pg-connection-menu .mud-menu-activator").KeyDownAsync(Bunit.Key.Enter);
        cut.WaitForAssertion(() => cut.FindAll(".pg-connection-option").Should().HaveCount(2));
        cut.FindAll(".pg-connection-option")[1].Click();
        cut.WaitForAssertion(() => cut.Find(".pg-selected-connection").TextContent.Should().Be("pg-analytics"));
    }

    private static bool ToggleShows(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string label) =>
        cut.FindAll(".pg-mode-toggle .mud-toggle-item").First(i => i.TextContent.Contains(label)).ClassList.Contains("mud-toggle-item-selected");

    private static IRenderedComponent<SbConsole.Plugins.Postgres.Pages.Query> Page(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindComponent<SbConsole.Plugins.Postgres.Pages.Query>();

    [Fact]
    public async Task Switching_connection_in_write_mode_returns_to_read_only_with_the_toggle_showing_it()
    {
        AddSecondConnection();
        Confirmation.ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderQuery();
        ClickWrite(cut);
        cut.WaitForElement(".pg-write-banner");
        ToggleShows(cut, "Write").Should().BeTrue();

        await SwitchToOtherAsync(cut);

        cut.WaitForAssertion(() => ToggleShows(cut, "Read-only").Should().BeTrue());
        ToggleShows(cut, "Write").Should().BeFalse();
        Run(cut);
        cut.WaitForElement(".pg-status");
        _ = Operations.Received().RunQueryAsync(OtherSecret, "app", Arg.Is<PgQueryRequest>(r => !r.Write), Arg.Any<CancellationToken>());
        _ = Operations.DidNotReceive().RunQueryAsync(OtherSecret, "app", Arg.Is<PgQueryRequest>(r => r.Write), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_run_from_the_previous_connection_that_completes_after_a_switch_is_not_rendered()
    {
        AddSecondConnection();
        var gate = new TaskCompletionSource<PgQueryResult>();
        Operations.RunQueryAsync(Secret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var cut = RenderQuery();
        Run(cut);
        cut.WaitForAssertion(() => cut.Find(".pg-run").HasAttribute("disabled").Should().BeTrue());

        await SwitchToOtherAsync(cut);
        gate.SetResult(Result());
        for (var i = 0; i < 10; i++)
        {
            await cut.InvokeAsync(() => Task.CompletedTask);
            await Task.Yield();
        }

        cut.FindAll(".pg-result-grid").Should().BeEmpty();
        cut.FindAll(".pg-status").Should().BeEmpty();
        cut.FindAll(".pg-history-item").Should().BeEmpty();
    }

    [Fact]
    public void Ctrl_enter_while_the_prod_confirmation_is_open_runs_read_only()
    {
        var gate = new TaskCompletionSource<bool>();
        Confirmation.ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var cut = RenderQuery();
        Type(cut, "update t set a = 1");
        ClickWrite(cut);
        cut.WaitForAssertion(() => Confirmation.Received(1).ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()));

        cut.InvokeAsync(() => Page(cut).Instance.RunFromShortcut());

        cut.WaitForAssertion(() => Operations.Received(1).RunQueryAsync(Secret, "app",
            Arg.Is<PgQueryRequest>(r => !r.Write && r.Sql == "update t set a = 1"), Arg.Any<CancellationToken>()));
        Operations.DidNotReceive().RunQueryAsync(Secret, "app", Arg.Is<PgQueryRequest>(r => r.Write), Arg.Any<CancellationToken>());
        gate.SetResult(false);
    }

    [Fact]
    public async Task Run_is_disabled_while_a_run_is_in_flight()
    {
        var gate = new TaskCompletionSource<PgQueryResult>();
        Operations.RunQueryAsync(Secret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var cut = RenderQuery();
        Run(cut);

        cut.WaitForAssertion(() => cut.Find(".pg-run").HasAttribute("disabled").Should().BeTrue());
        gate.SetResult(Result());
        cut.WaitForElement(".pg-result-grid");
        cut.Find(".pg-run").HasAttribute("disabled").Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_confirmation_that_resolves_true_after_a_connection_switch_does_not_enable_write_mode()
    {
        AddSecondConnection();
        var gate = new TaskCompletionSource<bool>();
        Confirmation.ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var cut = RenderQuery();
        ClickWrite(cut);
        cut.WaitForAssertion(() => Confirmation.Received(1).ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()));

        await SwitchToOtherAsync(cut);
        gate.SetResult(true);
        for (var i = 0; i < 10; i++)
        {
            await cut.InvokeAsync(() => Task.CompletedTask);
            await Task.Yield();
        }

        ToggleShows(cut, "Write").Should().BeFalse();
        Run(cut);
        cut.WaitForElement(".pg-status");
        _ = Operations.DidNotReceive().RunQueryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Is<PgQueryRequest>(r => r.Write), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Switching_database_cancels_the_in_flight_run_and_keeps_run_disabled_until_it_completes()
    {
        Operations.ListDatabasesAsync(Secret, Arg.Any<CancellationToken>()).Returns(["app", "other"]);
        Operations.RunQueryAsync(Secret, "other", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Result());
        var gate = new TaskCompletionSource<PgQueryResult>();
        var seen = new List<CancellationToken>();
        Operations.RunQueryAsync(Secret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            seen.Add(c.Arg<CancellationToken>());
            return gate.Task;
        });
        var cut = RenderQuery();
        cut.WaitForAssertion(() => cut.FindAll(".pg-database-picker").Should().HaveCount(1));
        Run(cut);
        cut.WaitForAssertion(() => seen.Should().HaveCount(1));

        cut.Find("div.mud-input-control.pg-database-picker").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".mud-list-item").Should().HaveCountGreaterThan(1));
        cut.FindAll(".mud-list-item").First(i => i.TextContent.Contains("other")).Click();

        cut.WaitForAssertion(() => seen[0].IsCancellationRequested.Should().BeTrue());
        cut.Find(".pg-run").HasAttribute("disabled").Should().BeTrue();

        gate.SetCanceled();
        cut.WaitForAssertion(() => cut.Find(".pg-run").HasAttribute("disabled").Should().BeFalse());
        cut.FindAll(".pg-result-grid").Should().BeEmpty();
    }

    [Fact]
    public void Json_tab_emits_numeric_and_boolean_columns_as_json_numbers_and_booleans()
    {
        Operations.RunQueryAsync(Secret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>()).Returns(_ => new PgQueryResult(
            [new PgResultColumn("n", "int8"), new PgResultColumn("ok", "bool"), new PgResultColumn("price", "numeric"), new PgResultColumn("s", "text"), new PgResultColumn("f", "float8")],
            [[Text("42"), Text("true"), Text("9.50"), Text("7"), Text("NaN")]],
            false, null, TimeSpan.FromSeconds(1), 1, "SELECT 1"));
        var cut = RenderQuery();
        Run(cut);
        cut.WaitForElement(".pg-result-grid");

        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("JSON")).Click();

        var json = cut.WaitForElement(".pg-result-json").TextContent;
        json.Should().Contain("\"n\": 42,").And.Contain("\"ok\": true").And.Contain("\"price\": 9.50").And.Contain("\"s\": \"7\"").And.Contain("\"f\": \"NaN\"");
    }

    [Fact]
    public async Task The_run_shortcut_is_registered_on_render_and_unregistered_on_dispose()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderQuery();

        cut.WaitForAssertion(() => module.VerifyInvoke("registerRunShortcut"));
        await DisposeComponentsAsync();

        module.VerifyInvoke("unregisterRunShortcut");
    }
}
