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
}
