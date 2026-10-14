using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

public class BrowsePageTests : PgPageTestBase
{
    private const uint Orders = 42;
    private const uint Customers = 41;

    private static PgObjectNode Table(uint oid, string name, long? estimate, string schema = "public") =>
        new(oid, schema, name, PgObjectKind.Table, estimate);

    private static PgSchemaTree DefaultTree() => new("app",
    [
        new PgSchemaNode("public",
            [Table(Customers, "customers", 2_100_000), Table(Orders, "orders", 41_200_000), Table(43, "order_items", 128_000_000)],
            [new PgObjectNode(50, "public", "v_order_totals", PgObjectKind.View, null)], [],
            [new PgObjectNode(60, "public", "fn_total", PgObjectKind.Function, null)]),
        new PgSchemaNode("reporting", [Table(70, "daily", 9_000, "reporting")], [], [], []),
    ]);

    private PgObjectDetail OrdersDetail(IReadOnlyList<PgIndex>? indexes = null) => new(
        Orders, "public", "orders", PgObjectKind.Table, 41_204_117, 38L << 30, 12L << 30, "app_rw",
        [
            new PgColumn(1, "id", "bigint", false, "nextval('orders_id_seq')", true, null, null),
            new PgColumn(2, "status", "text", false, null, false, null, "order state"),
            new PgColumn(3, "shipped_at", "timestamptz", true, null, false, null, null),
            new PgColumn(4, "metadata", "jsonb", true, null, false, null, null),
        ],
        indexes ?? [],
        [new PgConstraint("orders_pkey", "primary key", "PRIMARY KEY (id)")],
        Time.GetUtcNow().AddDays(-41));

    private static PgCell Text(string value) => new(PgCellKind.Text, value);

    private static IReadOnlyList<PgCell> Row(string id, string status = "pending") =>
        [Text(id), Text(status), new PgCell(PgCellKind.Null, null), new PgCell(PgCellKind.Json, "{…}", 4, "{ \"channel\": \"web\" }")];

    private static PgRowPage Page(int count, long? estimate = 184_212, int start = 1) => new(
        [new PgResultColumn("id", "int8"), new PgResultColumn("status", "text"), new PgResultColumn("shipped_at", "timestamptz"), new PgResultColumn("metadata", "jsonb")],
        Enumerable.Range(start, count).Select(i => Row(i.ToString())).ToList(), estimate);

    public BrowsePageTests()
    {
        Operations.ListDatabasesAsync(Secret, Arg.Any<CancellationToken>()).Returns(["app", "analytics"]);
        Operations.ListSchemaAsync(Secret, "app", Arg.Any<CancellationToken>()).Returns(_ => DefaultTree());
        Operations.GetObjectDetailAsync(Secret, "app", Orders, Arg.Any<CancellationToken>()).Returns(_ => OrdersDetail());
        Operations.PreviewRowsAsync(Secret, "app", Arg.Any<PgRowsRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Page(3));
    }

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderBrowse(string? query = null)
    {
        Nav.NavigateTo($"/p/postgres/browse?connectionId={ConnectionId}{query}");
        return RenderWithPopovers(b =>
        {
            b.OpenComponent<SbConsole.Plugins.Postgres.Pages.Browse>(0);
            b.CloseComponent();
        });
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderOrders(string extraQuery = "")
    {
        var cut = RenderBrowse($"&db=app&oid={Orders}{extraQuery}");
        cut.WaitForElement(".pg-object-title");
        return cut;
    }

    private static void SelectTab(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string text) =>
        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains(text)).Click();

    // Lets continuations released by a TaskCompletionSource run through the renderer's dispatcher before asserting.
    private static async Task FlushAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        for (var i = 0; i < 10; i++)
        {
            await cut.InvokeAsync(() => Task.CompletedTask);
            await Task.Yield();
        }
    }

    private void Pick(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, string text)
    {
        cut.Find(cssClass).MouseDown();
        cut.WaitForElement(".mud-popover-open .mud-list-item");
        cut.FindAll(".mud-popover-open .mud-list-item").First(i => i.TextContent.Trim() == text).Click();
    }

    private static void OpenGroup(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string title) =>
        cut.FindAll(".pg-tree-group").First(g => g.TextContent.Contains(title)).Click();

    private PgRowsRequest LastRequest() =>
        Operations.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IPostgresOperations.PreviewRowsAsync))
            .Select(c => (PgRowsRequest)c.GetArguments()[2]!).Last();

    [Fact]
    public void Tree_renders_schemas_groups_and_items_with_compact_estimates()
    {
        var cut = RenderBrowse("&db=app");
        cut.WaitForElement(".pg-tree-item");

        cut.FindAll(".pg-tree-schema").Select(e => e.TextContent).Should().Satisfy(
            a => a.Contains("public") && a.Contains("5"), b => b.Contains("reporting") && b.Contains("1"));
        cut.Find(".pg-tree-group").TextContent.Should().Contain("Tables").And.Contain("3");
        var items = cut.FindAll(".pg-tree-item").Select(e => e.TextContent).ToList();
        items.Should().Contain(t => t.Contains("customers") && t.Contains("2.1M"));
        items.Should().Contain(t => t.Contains("order_items") && t.Contains("128M"));
        cut.Markup.Should().Contain("Row badges are").And.Contain("reltuples");
    }

    [Fact]
    public void Only_the_first_50_tables_show_then_a_more_link_expands_the_rest()
    {
        var many = Enumerable.Range(1, 60).Select(i => Table((uint)(1000 + i), $"t{i:00}", i)).ToList();
        Operations.ListSchemaAsync(Secret, "app", Arg.Any<CancellationToken>())
            .Returns(new PgSchemaTree("app", [new PgSchemaNode("public", many, [], [], [])]));
        var cut = RenderBrowse("&db=app");
        cut.WaitForElement(".pg-tree-item");

        cut.FindAll(".pg-tree-item").Should().HaveCount(50);
        cut.Find(".pg-tree-more").TextContent.Should().Contain("10 more");
        cut.Find(".pg-tree-more").Click();
        cut.FindAll(".pg-tree-item").Should().HaveCount(60);
        cut.FindAll(".pg-tree-more").Should().BeEmpty();
    }

    [Fact]
    public void Filtering_narrows_the_tree_and_shows_x_of_y_counts()
    {
        var cut = RenderBrowse("&db=app");
        cut.WaitForElement(".pg-tree-item");

        cut.Find(".pg-tree-filter input").Input("ORD");
        Time.Advance(TimeSpan.FromMilliseconds(250)); // the filter is debounced on the injected TimeProvider

        cut.WaitForAssertion(() => cut.FindAll(".pg-tree-schema").Should().HaveCount(1));
        cut.FindAll(".pg-tree-item").Select(e => e.TextContent).Should().Satisfy(
            a => a.Contains("orders"), b => b.Contains("order_items"), c => c.Contains("v_order_totals"));
        cut.Find(".pg-tree-schema").TextContent.Should().Contain("3 of 5");
        cut.FindAll(".pg-tree-schema").Should().HaveCount(1, "reporting has no match");
    }

    [Fact]
    public void Clicking_an_item_loads_its_detail_and_puts_the_oid_in_the_url()
    {
        var cut = RenderBrowse("&db=app");
        cut.WaitForElement(".pg-tree-item");

        cut.FindAll(".pg-tree-item").First(e => e.TextContent.Contains("orders") && !e.TextContent.Contains("order_items")).Click();
        cut.WaitForElement(".pg-object-title");

        cut.Find(".pg-object-title").TextContent.Should().Be("public.orders");
        cut.Find(".pg-object-kind").TextContent.Should().Contain("table");
        var meta = cut.Find(".pg-object-meta").TextContent;
        meta.Should().Contain("41,204,117 rows").And.Contain("38 GB + 12 GB indexes").And.Contain("owner app_rw");
        cut.Markup.Should().Contain("reads only");
        Nav.Uri.Should().Contain($"oid={Orders}");
    }

    [Fact]
    public void An_oid_in_the_url_is_restored_on_load()
    {
        var cut = RenderOrders();

        cut.Find(".pg-object-title").TextContent.Should().Be("public.orders");
    }

    [Fact]
    public void An_object_that_no_longer_exists_shows_a_friendly_message()
    {
        Operations.GetObjectDetailAsync(Secret, "app", 999, Arg.Any<CancellationToken>()).Returns((PgObjectDetail?)null);
        var cut = RenderBrowse("&db=app&oid=999");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("no longer exists"));
    }

    [Fact]
    public void Data_grid_shows_null_chips_and_json_key_counts_and_expands_json()
    {
        var cut = RenderOrders();
        cut.WaitForElement(".pg-data-grid .pg-null");

        cut.FindAll(".pg-data-grid .pg-null").Should().HaveCount(3);
        cut.FindAll(".pg-json-toggle")[0].TextContent.Should().Contain("4 keys");
        cut.FindAll("pre.pg-json").Should().BeEmpty();

        cut.FindAll(".pg-json-toggle")[0].Click();

        cut.Find("pre.pg-json").TextContent.Should().Contain("\"channel\"");
        cut.FindAll(".pg-json-toggle")[0].Click();
        cut.FindAll("pre.pg-json").Should().BeEmpty();
    }

    [Fact]
    public void Data_footer_shows_the_range_and_the_estimate()
    {
        var cut = RenderOrders();
        cut.WaitForElement(".pg-data-footer");

        var footer = cut.Find(".pg-data-footer").TextContent;
        footer.Should().Contain("Rows 1–3 of 184,212 matching ≈").And.Contain("1 / 3,685");
    }

    [Fact]
    public void Applying_a_filter_value_requests_rows_with_that_filter_from_offset_zero()
    {
        var cut = RenderOrders();
        cut.WaitForElement(".pg-filter-value-1");

        cut.Find(".pg-filter-value-1 input").Change("pending");

        cut.WaitForAssertion(() => LastRequest().Filters.Should().Equal(new PgFilter("status", PgFilterOp.Eq, "pending")));
        LastRequest().Offset.Should().Be(0);
        cut.Markup.Should().Contain("Filters").And.Contain("pg-filter-count");
    }

    [Fact]
    public void Changing_an_operator_to_is_null_applies_immediately_without_a_value()
    {
        var cut = RenderOrders();
        cut.WaitForElement(".pg-filter-op-2");

        cut.Find(".pg-filter-op-2").MouseDown();
        cut.WaitForElement(".mud-popover-open .mud-list-item");
        cut.FindAll(".mud-popover-open .mud-list-item").First(i => i.TextContent.Trim() == "is null").Click();

        cut.WaitForAssertion(() => LastRequest().Filters.Should().Equal(new PgFilter("shipped_at", PgFilterOp.IsNull, null)));
    }

    [Fact]
    public void A_refused_filter_shows_an_alert_and_keeps_the_last_good_rows()
    {
        var cut = RenderOrders();
        cut.WaitForElement(".pg-filter-value-1");
        Operations.PreviewRowsAsync(Secret, "app", Arg.Is<PgRowsRequest>(r => r.Filters.Count == 1), Arg.Any<CancellationToken>())
            .Returns<PgRowPage>(_ => throw new PgSqlRefusedException("Filter on this column is not allowed."));

        cut.Find(".pg-filter-value-1 input").Change("x");

        cut.WaitForElement(".pg-rows-error").TextContent.Should().Contain("not allowed");
        cut.FindAll(".pg-data-grid tbody tr").Should().HaveCount(3);
    }

    [Fact]
    public void Clicking_a_header_sorts_ascending_then_descending()
    {
        var cut = RenderOrders();
        cut.WaitForElement(".pg-sort");

        cut.FindAll(".pg-sort")[0].Click();
        cut.WaitForAssertion(() => LastRequest().Should().Match<PgRowsRequest>(r => r.SortColumn == "id" && !r.SortDescending));
        cut.Find(".pg-sort-chip").TextContent.Should().Contain("id").And.Contain("asc");

        cut.FindAll(".pg-sort")[0].Click();
        cut.WaitForAssertion(() => LastRequest().Should().Match<PgRowsRequest>(r => r.SortColumn == "id" && r.SortDescending));
        cut.Find(".pg-sort-chip").TextContent.Should().Contain("desc");
    }

    [Fact]
    public void Next_page_requests_offset_50_and_prev_goes_back()
    {
        Operations.PreviewRowsAsync(Secret, "app", Arg.Any<PgRowsRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => Page(50, 184_212, (int)c.Arg<PgRowsRequest>().Offset + 1));
        var cut = RenderOrders();
        cut.WaitForElement(".pg-data-next");
        cut.Find(".pg-data-prev").HasAttribute("disabled").Should().BeTrue();

        cut.Find(".pg-data-next").Click();

        cut.WaitForAssertion(() => LastRequest().Offset.Should().Be(50));
        cut.Find(".pg-data-footer").TextContent.Should().Contain("Rows 51–100").And.Contain("2 / 3,685");
        cut.Find(".pg-data-prev").Click();
        cut.WaitForAssertion(() => LastRequest().Offset.Should().Be(0));
    }

    [Fact]
    public void Indexes_tab_shows_flags_summary_and_an_unused_chip()
    {
        var indexes = new List<PgIndex>
        {
            new(1, "orders_pkey", "CREATE UNIQUE INDEX orders_pkey ON public.orders USING btree (id)", "(id)", "btree", 2L << 30, 812, 812, true, true, false, true, true),
            new(2, "idx_legacy", "CREATE INDEX idx_legacy ON public.orders USING btree (legacy_ref)", "(legacy_ref)", "btree", 6L << 30, 0, 0, false, false, false, true, false),
            new(3, "idx_partial", "CREATE INDEX idx_partial ON public.orders (created_at) WHERE status = 'pending'", "(created_at)", "btree", 41L << 20, 5, 5, false, false, true, true, false),
            new(4, "idx_broken", "CREATE INDEX idx_broken ON public.orders (x)", "(x)", "btree", 1L << 20, 5, 5, false, false, false, false, false),
        };
        Operations.GetObjectDetailAsync(Secret, "app", Orders, Arg.Any<CancellationToken>()).Returns(OrdersDetail(indexes));
        var cut = RenderOrders();

        SelectTab(cut, "Indexes");

        cut.WaitForElement(".pg-index-row");
        cut.Find(".pg-index-summary").TextContent.Should().Contain("pg_stat_user_indexes").And.Contain("stats reset 41 days ago").And.Contain("1 unused").And.Contain("6 GB");
        var rows = cut.FindAll(".pg-index-row");
        rows.Should().HaveCount(4);
        cut.FindAll("tr tr").Should().BeEmpty("MudTable already wraps the row template in a <tr>");
        rows[0].TextContent.Should().Contain("primary").And.Contain("unique");
        rows[1].TextContent.Should().Contain("unused 41d");
        rows[2].TextContent.Should().Contain("partial");
        rows[3].TextContent.Should().Contain("invalid");
        rows[0].TextContent.Should().NotContain("unused");
    }

    [Fact]
    public void Copy_definition_writes_the_index_definition_to_the_clipboard()
    {
        var def = "CREATE INDEX idx_legacy ON public.orders USING btree (legacy_ref)";
        Operations.GetObjectDetailAsync(Secret, "app", Orders, Arg.Any<CancellationToken>()).Returns(OrdersDetail(
            [new PgIndex(2, "idx_legacy", def, "(legacy_ref)", "btree", 1, 0, 0, false, false, false, true, false)]));
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetVoidResult();
        var cut = RenderOrders();
        SelectTab(cut, "Indexes");
        cut.WaitForElement(".pg-index-menu");

        cut.Find(".pg-index-menu button").Click();
        cut.WaitForElement(".pg-index-copy").Click();

        JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments[0].Should().Be(def);
    }

    [Fact]
    public void Columns_and_constraints_tabs_list_their_rows()
    {
        var cut = RenderOrders();

        SelectTab(cut, "Columns");
        cut.WaitForElement(".pg-columns");
        cut.Find(".pg-columns").TextContent.Should().Contain("nextval('orders_id_seq')").And.Contain("order state").And.Contain("PK");

        SelectTab(cut, "Constraints");
        cut.WaitForElement(".pg-constraints");
        cut.Find(".pg-constraints").TextContent.Should().Contain("PRIMARY KEY (id)");
    }

    [Fact]
    public void Ddl_tab_loads_and_shows_the_text()
    {
        Operations.GetDdlAsync(Secret, "app", Orders, Arg.Any<CancellationToken>()).Returns("CREATE TABLE public.orders (id bigint);");
        var cut = RenderOrders();

        SelectTab(cut, "DDL");

        cut.WaitForElement("pre.pg-ddl").TextContent.Should().Contain("CREATE TABLE public.orders");
        cut.FindAll(".pg-ddl-copy").Should().HaveCount(1);
    }

    [Fact]
    public void Open_in_query_links_to_the_query_page_with_a_select_for_the_object()
    {
        var cut = RenderOrders();

        var href = cut.Find(".pg-open-in-query").GetAttribute("href")!;

        href.Should().Be($"/p/postgres/query?connectionId={ConnectionId}&db=app&sql=" + Uri.EscapeDataString("SELECT * FROM \"public\".\"orders\" LIMIT 100"));
    }

    [Fact]
    public void Export_csv_runs_the_handler_with_the_current_filters_and_downloads_through_the_js_module()
    {
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Postgres/postgres.js");
        module.SetupVoid("downloadFromStream", _ => true).SetVoidResult();
        Operations.ExportTableCsvAsync(Secret, "app", Arg.Any<PgRowsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CsvExport("orders.csv", [1, 2, 3], 3, false));
        var cut = RenderOrders();
        cut.Find(".pg-filter-value-1 input").Change("pending");
        cut.WaitForAssertion(() => LastRequest().Filters.Should().HaveCount(1));

        cut.Find(".pg-export-csv").Click();

        cut.WaitForAssertion(() => module.VerifyInvoke("downloadFromStream").Arguments[0].Should().Be("orders.csv"));
        Operations.Received(1).ExportTableCsvAsync(Secret, "app",
            Arg.Is<PgRowsRequest>(r => r.Oid == Orders && r.Filters.Count == 1), Arg.Any<CancellationToken>());
        Audit.Received(1).RecordAsync("pg.table.export", $"{ConnectionName}/app/public.orders", Arg.Any<SbConsole.Sdk.ActionRisk>(),
            true, Arg.Is<string?>(s => s!.Contains("1 filters")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_connection_with_no_databases_shows_a_friendly_state_and_loads_nothing()
    {
        Operations.ListDatabasesAsync(Secret, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        var cut = RenderBrowse();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No database selected"));
        Operations.DidNotReceiveWithAnyArgs().ListSchemaAsync(default!, default, default);
    }

    [Fact]
    public void A_tree_load_failure_shows_an_error_with_retry()
    {
        Operations.ListSchemaAsync(Secret, "app", Arg.Any<CancellationToken>()).Returns<PgSchemaTree>(_ => throw new TimeoutException());
        var cut = RenderBrowse("&db=app");

        cut.WaitForElement(".pg-browse-error").TextContent.Should().Contain("Can't reach the server");
        cut.FindAll(".pg-browse-retry").Should().HaveCount(1);
    }

    [Fact]
    public async Task A_slow_older_detail_load_cannot_overwrite_a_newer_selection()
    {
        var slow = new TaskCompletionSource<PgObjectDetail?>();
        Operations.GetObjectDetailAsync(Secret, "app", Customers, Arg.Any<CancellationToken>()).Returns(_ => slow.Task);
        var cut = RenderBrowse("&db=app");
        cut.WaitForElement(".pg-tree-item");
        cut.FindAll(".pg-tree-item").First(e => e.TextContent.Contains("customers")).Click();

        cut.FindAll(".pg-tree-item").First(e => e.TextContent.Trim().StartsWith("orders")).Click();
        cut.WaitForAssertion(() => cut.Find(".pg-object-title").TextContent.Should().Be("public.orders"));
        slow.SetResult(OrdersDetail() with { Oid = Customers, Name = "customers" });
        await FlushAsync(cut);

        cut.Find(".pg-object-title").TextContent.Should().Be("public.orders");
    }

    [Fact]
    public void Changing_database_clears_the_selection_and_reloads_the_tree_for_the_new_database()
    {
        Operations.ListSchemaAsync(Secret, "analytics", Arg.Any<CancellationToken>())
            .Returns(new PgSchemaTree("analytics", [new PgSchemaNode("public", [Table(900, "events", 5)], [], [], [])]));
        var cut = RenderOrders();

        Pick(cut, ".pg-database-picker", "analytics");

        cut.WaitForAssertion(() => cut.FindAll(".pg-tree-item").Should().ContainSingle().Which.TextContent.Should().Contain("events"));
        cut.FindAll(".pg-object-title").Should().BeEmpty();
        cut.Markup.Should().Contain("Select a table, view or function");
        Operations.Received().ListSchemaAsync(Secret, "analytics", Arg.Any<CancellationToken>());
        Nav.Uri.Should().Contain("db=analytics").And.NotContain("oid=");
    }

    [Fact]
    public void An_oid_from_another_database_is_dropped_when_the_resolved_database_differs_from_the_url()
    {
        var cut = RenderBrowse($"&db=nope&oid={Orders}");

        cut.WaitForElement(".pg-tree-item");
        cut.FindAll(".pg-object-title").Should().BeEmpty();
        Operations.DidNotReceive().GetObjectDetailAsync(Secret, Arg.Any<string>(), Orders, Arg.Any<CancellationToken>());
        Nav.Uri.Should().NotContain("oid=");
    }

    [Fact]
    public void Tab_indexes_in_the_url_restores_the_indexes_tab()
    {
        var cut = RenderOrders("&tab=indexes");

        cut.WaitForElement(".pg-index-summary");
        cut.FindAll(".pg-data-grid").Should().BeEmpty();
    }

    private void ArrangeFunction()
    {
        Operations.GetObjectDetailAsync(Secret, "app", 60, Arg.Any<CancellationToken>()).Returns(new PgObjectDetail(
            60, "public", "fn_total", PgObjectKind.Function, null, 0, 0, "app_rw", [], [], [], null));
        Operations.GetDdlAsync(Secret, "app", 60, Arg.Any<CancellationToken>()).Returns("CREATE FUNCTION public.fn_total() ...");
    }

    [Fact]
    public void Selecting_a_function_switches_to_ddl_and_the_next_table_restores_the_previous_tab()
    {
        ArrangeFunction();
        var cut = RenderOrders();
        cut.WaitForElement(".pg-data-grid");

        OpenGroup(cut, "Functions");
        cut.FindAll(".pg-tree-item").First(e => e.TextContent.Contains("fn_total")).Click();
        cut.WaitForElement("pre.pg-ddl").TextContent.Should().Contain("CREATE FUNCTION");
        Nav.Uri.Should().Contain("tab=ddl");

        cut.FindAll(".pg-tree-item").First(e => e.TextContent.Trim().StartsWith("orders")).Click();
        cut.WaitForElement(".pg-data-grid");
        cut.FindAll("pre.pg-ddl").Should().BeEmpty();
        Nav.Uri.Should().NotContain("tab=");
    }

    [Fact]
    public void Open_in_query_and_export_are_hidden_for_functions_but_shown_for_views()
    {
        ArrangeFunction();
        Operations.GetObjectDetailAsync(Secret, "app", 50, Arg.Any<CancellationToken>()).Returns(new PgObjectDetail(
            50, "public", "v_order_totals", PgObjectKind.View, null, 0, 0, "app_rw", [new PgColumn(1, "id", "bigint", true, null, false, null, null)], [], [], null));
        var cut = RenderOrders();
        OpenGroup(cut, "Views");
        cut.FindAll(".pg-tree-item").First(e => e.TextContent.Contains("v_order_totals")).Click();
        cut.WaitForAssertion(() => cut.Find(".pg-object-title").TextContent.Should().Be("public.v_order_totals"));
        cut.FindAll(".pg-open-in-query").Should().HaveCount(1);
        cut.FindAll(".pg-export-csv").Should().HaveCount(1);

        OpenGroup(cut, "Functions");
        cut.FindAll(".pg-tree-item").First(e => e.TextContent.Contains("fn_total")).Click();

        cut.WaitForAssertion(() => cut.Find(".pg-object-title").TextContent.Should().Be("public.fn_total"));
        cut.FindAll(".pg-open-in-query").Should().BeEmpty();
        cut.FindAll(".pg-export-csv").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_export_shows_an_error_alert_and_changing_database_clears_it()
    {
        Operations.ExportTableCsvAsync(Secret, "app", Arg.Any<PgRowsRequest>(), Arg.Any<CancellationToken>())
            .Returns<CsvExport>(_ => throw new TimeoutException());
        var cut = RenderOrders();

        cut.Find(".pg-export-csv").Click();

        var alert = cut.WaitForElement(".pg-export-status");
        alert.ClassName.Should().Contain("error");
        alert.TextContent.Should().NotBeNullOrWhiteSpace();

        Pick(cut, ".pg-database-picker", "analytics");
        cut.WaitForAssertion(() => cut.FindAll(".pg-export-status").Should().BeEmpty());
    }

    [Fact]
    public void Blurring_a_filter_value_applies_it()
    {
        var cut = RenderOrders();
        cut.WaitForElement(".pg-filter-value-1");
        var input = cut.Find(".pg-filter-value-1 input");

        input.Change("pending");
        input.Blur();

        cut.WaitForAssertion(() => LastRequest().Filters.Should().Equal(new PgFilter("status", PgFilterOp.Eq, "pending")));
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IPostgresOperations.PreviewRowsAsync)
                                              && ((PgRowsRequest)c.GetArguments()[2]!).Filters.Count == 1).Should().Be(1, "blur after the commit must not reload");
    }
}
