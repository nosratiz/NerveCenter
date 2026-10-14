using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

public class MaintenancePageTests : PgPageTestBase
{
    private const long Gb = 1024L * 1024 * 1024;

    private PgTableHealth T(uint oid, string name, double dead, long? bloat = null, bool running = false, long size = 10 * Gb,
        DateTimeOffset? lastAuto = null) =>
        new(oid, "public", name, size, 1000, (long)(dead * 10), dead, bloat, null, lastAuto, null, null, running);

    private PgMaintenance Data(IReadOnlyList<PgTableHealth>? tables = null, bool stattuple = false, long xid = 184_000_000,
        IReadOnlyList<PgVacuumProgress>? running = null) =>
        new(true, 3, running ?? [], xid, 200_000_000, stattuple,
            tables ?? [T(1, "order_items", 41), T(2, "sessions", 38), T(3, "orders", 8), T(4, "products", 1)], Time.GetUtcNow());

    private void Arrange(PgMaintenance? data = null, string mode = "read-write")
    {
        Operations.ListDatabasesAsync(Secret, Arg.Any<CancellationToken>()).Returns(["app", "analytics"]);
        Operations.GetMaintenanceAsync(Secret, "app", Arg.Any<CancellationToken>()).Returns(_ => data ?? Data());
        if (mode != "read-write")
        {
            Connections.ListAsync("postgres", Arg.Any<CancellationToken>()).Returns(
            [
                new ConnectionInfo(ConnectionId, ConnectionName, "postgres", ["prod"], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                    new Dictionary<string, string> { ["Server"] = "db:5432", ["Database"] = "app", ["Mode"] = mode }),
            ]);
        }
    }

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(string query = "&db=app")
    {
        Nav.NavigateTo($"/p/postgres/maintenance?connectionId={ConnectionId}{query}");
        var cut = RenderWithPopovers(b =>
        {
            b.OpenComponent<SbConsole.Plugins.Postgres.Pages.Maintenance>(0);
            b.CloseComponent();
        });
        cut.WaitForState(() => cut.FindAll(".pg-tile").Count > 0 || cut.FindAll(".pg-maintenance-error").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static string Text(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string selector) => cut.Find(selector).TextContent;

    [Fact]
    public void Header_chip_and_tiles_summarise_the_database()
    {
        Arrange(Data(running: [new PgVacuumProgress(49210, "public.order_items", Time.GetUtcNow().AddMinutes(-22), 41.0)]));
        var cut = RenderPage();

        Text(cut, ".pg-autovacuum-chip").Should().Be("autovacuum on · 3 workers · 1 running");
        Text(cut, ".pg-tile-flagged").Should().Contain("2").And.Contain("> 30% dead tuples or > 20% bloat est.");
        Text(cut, ".pg-tile-reclaimable").Should().Contain("—").And.Contain("install pgstattuple for bloat estimates");
        Text(cut, ".pg-tile-autovacuum").Should().Contain("public.order_items").And.Contain("pid 49210").And.Contain("41% of heap scanned").And.Contain("ago");
        Text(cut, ".pg-tile-xid").Should().Contain("184M").And.Contain("wraparound at 2.1B · freeze_max_age 200M");
        cut.Find(".pg-tile-xid").ClassList.Should().NotContain("pg-warning");
    }

    [Fact]
    public void Idle_autovacuum_and_xid_past_freeze_max_age_warn()
    {
        Arrange(Data(xid: 250_000_000));
        var cut = RenderPage();

        Text(cut, ".pg-tile-autovacuum").Should().Contain("idle");
        cut.Find(".pg-tile-xid").ClassList.Should().Contain("pg-warning");
    }

    [Fact]
    public void Bloat_flags_a_table_and_sums_into_the_reclaimable_tile()
    {
        Arrange(Data([T(1, "a", 5, bloat: 3 * Gb, size: 10 * Gb), T(2, "b", 2, bloat: 1 * Gb, size: 10 * Gb)], stattuple: true));
        var cut = RenderPage();

        Text(cut, ".pg-tile-flagged").Should().Contain("1");
        Text(cut, ".pg-tile-reclaimable").Should().Contain("4 GB").And.NotContain("install pgstattuple");
    }

    [Fact]
    public void Rows_show_the_top_25_then_a_more_row_that_expands()
    {
        Arrange(Data(Enumerable.Range(1, 30).Select(i => T((uint)i, $"t{i}", 0.5)).ToList()));
        var cut = RenderPage();

        cut.FindAll(".pg-maintenance-row").Should().HaveCount(25);
        System.Text.RegularExpressions.Regex.Replace(Text(cut, ".pg-maintenance-more"), @"\s+", " ").Trim().Should().Be("5 more tables under 1% dead · show all");

        cut.Find(".pg-maintenance-more button").Click();

        cut.FindAll(".pg-maintenance-row").Should().HaveCount(30);
    }

    [Fact]
    public void Dead_percent_over_30_gets_a_warning_chip_and_autovacuum_a_running_chip()
    {
        Arrange(Data([T(1, "hot", 41, running: true), T(2, "cold", 8, lastAuto: Time.GetUtcNow().AddHours(-3))]));
        var cut = RenderPage();

        var rows = cut.FindAll(".pg-maintenance-row");
        rows[0].QuerySelector(".pg-dead-chip")!.TextContent.Should().Contain("41%");
        rows[0].QuerySelector(".pg-running-chip")!.TextContent.Should().Be("running");
        rows[1].QuerySelector(".pg-dead-chip").Should().BeNull();
        rows[1].TextContent.Should().Contain("3h 00m ago");
        rows[1].TextContent.Should().Contain("—");
    }

    [Fact]
    public void Row_buttons_confirm_plainly_and_call_the_handler_with_the_kind()
    {
        Arrange();
        Operations.RunMaintenanceAsync(Secret, "app", 2, PgMaintenanceKind.Analyze, Arg.Any<CancellationToken>()).Returns("\"public\".\"sessions\"");
        Confirmation.ConfirmAsync("ANALYZE", "public.sessions", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        Confirmation.ConfirmAsync("VACUUM", "public.order_items", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        Operations.RunMaintenanceAsync(Secret, "app", 1, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>()).Returns("\"public\".\"order_items\"");
        var cut = RenderPage();
        var before = Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IPostgresOperations.GetMaintenanceAsync));

        cut.FindAll(".pg-maintenance-row")[1].QuerySelector(".pg-analyze")!.Click();
        cut.WaitForAssertion(() => Operations.Received(1).RunMaintenanceAsync(Secret, "app", 2, PgMaintenanceKind.Analyze, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IPostgresOperations.GetMaintenanceAsync)).Should().Be(before + 1));

        cut.FindAll(".pg-maintenance-row")[0].QuerySelector(".pg-vacuum")!.Click();
        cut.WaitForAssertion(() => Operations.Received(1).RunMaintenanceAsync(Secret, "app", 1, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void A_declined_confirmation_runs_nothing()
    {
        Arrange();
        var cut = RenderPage();

        cut.FindAll(".pg-maintenance-row")[0].QuerySelector(".pg-vacuum")!.Click();

        cut.WaitForAssertion(() => Confirmation.Received(1).ConfirmAsync("VACUUM", "public.order_items", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()));
        Operations.DidNotReceiveWithAnyArgs().RunMaintenanceAsync(default!, default, default, default, default);
    }

    [Fact]
    public void Vacuum_full_sends_a_destructive_request_and_runs_on_confirm()
    {
        Arrange();
        ConfirmationRequest? seen = null;
        Confirmation.ConfirmAsync(Arg.Do<ConfirmationRequest>(r => seen = r), Arg.Any<CancellationToken>()).Returns(true);
        Operations.RunMaintenanceAsync(Secret, "app", 1, PgMaintenanceKind.VacuumFull, Arg.Any<CancellationToken>()).Returns("\"public\".\"order_items\"");
        var cut = RenderPage();

        cut.FindAll(".pg-maintenance-row")[0].QuerySelector(".pg-maintenance-menu button")!.Click();
        cut.WaitForElement(".mud-popover-open .pg-vacuum-full").Click();

        cut.WaitForAssertion(() => Operations.Received(1).RunMaintenanceAsync(Secret, "app", 1, PgMaintenanceKind.VacuumFull, Arg.Any<CancellationToken>()));
        seen.Should().NotBeNull();
        seen!.Verb.Should().Be("VACUUM FULL");
        seen.Target.Should().Be("public.order_items");
        seen.IsProd.Should().BeTrue();
        seen.Destructive.Should().BeTrue();
        seen.AuditAction.Should().Be("pg.maintenance.vacuum_full");
        seen.Consequence.Should().Be("Takes an ACCESS EXCLUSIVE lock and rewrites the table — reads and writes wait until it finishes.");
        seen.Details.Should().Contain(d => d.Label == "size").And.Contain(d => d.Label == "dead");
    }

    [Fact]
    public void Bulk_buttons_run_flagged_tables_sequentially_with_one_confirm()
    {
        Arrange();
        Confirmation.ConfirmAsync("VACUUM", "2 flagged tables", false, 2, Arg.Any<CancellationToken>()).Returns(true);
        var first = new TaskCompletionSource<string>();
        Operations.RunMaintenanceAsync(Secret, "app", 1, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>()).Returns(_ => first.Task);
        Operations.RunMaintenanceAsync(Secret, "app", 2, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>()).Returns("\"public\".\"sessions\"");
        var cut = RenderPage();
        Text(cut, ".pg-vacuum-flagged").Should().Be("VACUUM 2 flagged");

        cut.Find(".pg-vacuum-flagged").Click();

        cut.WaitForAssertion(() => Operations.Received(1).RunMaintenanceAsync(Secret, "app", 1, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>()));
        Operations.DidNotReceive().RunMaintenanceAsync(Secret, "app", 2, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => Text(cut, ".pg-bulk-progress").Should().Be("VACUUM 1 / 2…"));
        first.SetResult("\"public\".\"order_items\"");
        cut.WaitForAssertion(() => Operations.Received(1).RunMaintenanceAsync(Secret, "app", 2, PgMaintenanceKind.Vacuum, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.FindAll(".pg-bulk-progress").Should().BeEmpty());
    }

    [Fact]
    public void Bulk_stops_on_the_first_failure()
    {
        Arrange();
        Confirmation.ConfirmAsync("ANALYZE", "2 flagged tables", false, 2, Arg.Any<CancellationToken>()).Returns(true);
        Operations.RunMaintenanceAsync(Secret, "app", 1, PgMaintenanceKind.Analyze, Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));
        var cut = RenderPage();

        cut.Find(".pg-analyze-flagged").Click();

        cut.WaitForAssertion(() => Operations.Received(1).RunMaintenanceAsync(Secret, "app", 1, PgMaintenanceKind.Analyze, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.FindAll(".pg-bulk-progress").Should().BeEmpty());
        Operations.DidNotReceive().RunMaintenanceAsync(Secret, "app", 2, PgMaintenanceKind.Analyze, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_read_only_connection_disables_every_action()
    {
        Arrange(mode: "read-only");
        var cut = RenderPage();

        cut.Find(".pg-vacuum-flagged").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".pg-analyze-flagged").HasAttribute("disabled").Should().BeTrue();
        var row = cut.FindAll(".pg-maintenance-row")[0];
        row.QuerySelector(".pg-vacuum")!.HasAttribute("disabled").Should().BeTrue();
        row.QuerySelector(".pg-analyze")!.HasAttribute("disabled").Should().BeTrue();
        row.QuerySelector(".pg-vacuum")!.GetAttribute("title").Should().Be("Connection is read-only");
    }

    [Fact]
    public void A_load_failure_shows_the_error_without_tiles()
    {
        Operations.ListDatabasesAsync(Secret, Arg.Any<CancellationToken>()).Returns(["app"]);
        Operations.GetMaintenanceAsync(Secret, "app", Arg.Any<CancellationToken>()).Returns<Task<PgMaintenance>>(_ => throw new InvalidOperationException("nope"));
        var cut = RenderPage();

        cut.FindAll(".pg-maintenance-error").Should().HaveCount(1);
        cut.FindAll(".pg-tile").Should().BeEmpty();
    }

    [Fact]
    public void The_footnote_explains_the_risk_levels()
    {
        Arrange();
        var cut = RenderPage();

        Text(cut, ".pg-maintenance-footnote").Should().Contain("VACUUM and ANALYZE are Mutating: a short confirm, no typing.")
            .And.Contain("pg.maintenance.vacuum").And.Contain(".analyze");
    }
}
