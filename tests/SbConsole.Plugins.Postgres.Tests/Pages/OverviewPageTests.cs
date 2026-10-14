using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Overview;
using SbConsole.Plugins.Postgres.Tests.Overview;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

// The page type is fully qualified: "Overview" would otherwise resolve to the Overview namespace.
public class OverviewPageTests : PgPageTestBase
{
    private static readonly DateTimeOffset T0 = OverviewTestData.T0;

    private static PgAttention FullAttention() => new(
        3, TimeSpan.FromSeconds(580), 4711, "public.orders",
        [new PgIdleTransaction(48213, "app_rw", "api", TimeSpan.FromMinutes(14)), new PgIdleTransaction(47990, "app_rw", "api", TimeSpan.FromMinutes(6))],
        [new PgDeadTupleTable("order_items", 41), new PgDeadTupleTable("sessions", 38)],
        6, 13_314_398_617, "idx_orders_legacy_ref", 6_550_000_000, T0.AddDays(-41));

    private void Arrange(PgOverview overview) =>
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(overview);

    private void ArrangeFailure() =>
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<PgOverview>(_ => throw new TimeoutException("connect timed out"));

    private IRenderedComponent<SbConsole.Plugins.Postgres.Pages.Overview> RenderPage()
    {
        var cut = Render<SbConsole.Plugins.Postgres.Pages.Overview>();
        cut.WaitForState(() => cut.FindAll(".pg-overview-skeleton").Count == 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    [Fact]
    public void Tiles_render_captions_from_the_overview()
    {
        Arrange(OverviewTestData.Overview(connections: 312, maxConnections: 400));
        var cut = RenderPage();

        var connections = cut.Find(".pg-tile-connections").TextContent;
        connections.Should().Contain("312").And.Contain("/ 400 max_connections").And.Contain("78% · 2 idle · 0 waiting");
        cut.Find(".pg-tile-cache").TextContent.Should().Contain("99.0%").And.Contain("shared_buffers 128MB");
        var longest = cut.Find(".pg-tile-longest").TextContent;
        longest.Should().Contain("7m 00s").And.Contain("pid 42 · app · active").And.Contain("View in Activity →");
        cut.Find(".pg-tile-size").TextContent.Should().Contain("118 MB").And.Contain("2 databases");
        cut.Find(".pg-tile-replication").TextContent.Should().Contain("—").And.Contain("no replicas");
        cut.Find(".pg-overview-meta").TextContent.Should().Be("PostgreSQL 16.4 · primary · up 3d");
    }

    [Fact]
    public void Replication_tile_shows_lag_and_replica_count()
    {
        Arrange(OverviewTestData.Overview(replication: new PgReplication(2, TimeSpan.FromSeconds(0.8), "replica-2", 3_435_973_836)));
        var cut = RenderPage();

        cut.Find(".pg-tile-replication").TextContent.Should().Contain("0.8 s").And.Contain("2 streaming replicas · WAL 3.2 GB retained");
    }

    [Fact]
    public void Longest_transaction_tile_is_outlined_as_a_warning_when_over_five_minutes()
    {
        Arrange(OverviewTestData.Overview());
        RenderPage().Find(".pg-tile-longest").ClassList.Should().Contain("pg-warning");
    }

    [Fact]
    public void Longest_transaction_tile_is_calm_under_five_minutes()
    {
        var o = OverviewTestData.Overview() with { LongestTransaction = new PgLongestTransaction(7, "app", "w", "active", TimeSpan.FromMinutes(1)) };
        Arrange(o);
        RenderPage().Find(".pg-tile-longest").ClassList.Should().NotContain("pg-warning");
    }

    [Fact]
    public void Attention_items_are_ordered_and_link_with_the_connection_id()
    {
        Arrange(OverviewTestData.Overview(attention: FullAttention()));
        var cut = RenderPage();

        var items = cut.FindAll(".pg-attention-item");
        items.Should().HaveCount(4);
        items.Select(i => i.QuerySelector(".pg-attention-chip")!.TextContent.Trim())
            .Should().Equal("blocked", "idle in txn", "bloat", "unused idx");
        items[0].TextContent.Should().Contain("3 sessions blocked for up to 9m 40s").And.Contain("public.orders");
        items[1].TextContent.Should().Contain("2 sessions idle in transaction").And.Contain("pid 48213 (14m 00s)");
        items[2].TextContent.Should().Contain("2 tables over 30% dead tuples").And.Contain("order_items 41%");
        items[3].TextContent.Should().Contain("6 indexes with 0 scans since stats reset (41d)").And.Contain("largest idx_orders_legacy_ref");
        var hrefs = items.Select(i => i.QuerySelector("a")!.GetAttribute("href")).ToList();
        hrefs.Should().Equal(
            $"/p/postgres/activity?connectionId={ConnectionId}", $"/p/postgres/activity?connectionId={ConnectionId}",
            $"/p/postgres/maintenance?connectionId={ConnectionId}", $"/p/postgres/browse?connectionId={ConnectionId}");
        cut.Find(".pg-attention .pg-attention-count").TextContent.Should().Be("4");
    }

    [Fact]
    public void Attention_shows_a_friendly_empty_state()
    {
        Arrange(OverviewTestData.Overview());
        var cut = RenderPage();

        cut.Find(".pg-attention-empty").TextContent.Should().Be("Nothing needs attention.");
        cut.FindAll(".pg-attention-item").Should().BeEmpty();
    }

    [Fact]
    public void Databases_table_shows_dash_for_commits_per_second_until_a_second_sample_exists()
    {
        Arrange(OverviewTestData.Overview(captured: T0, commits: 100));
        var cut = RenderPage();
        var first = cut.FindAll(".pg-databases tbody tr").Select(r => r.TextContent).ToList();
        first.Should().HaveCount(2);
        first[0].Should().Contain("app").And.Contain("1000 B").And.Contain("owner").And.Contain("97.0%").And.Contain("—");

        Time.Advance(TimeSpan.FromSeconds(10));
        Arrange(OverviewTestData.Overview(captured: T0.AddSeconds(10), commits: 600));
        cut.Find(".pg-overview-refresh").Click();
        cut.WaitForAssertion(() => cut.FindAll(".pg-databases tbody tr")[0].TextContent.Should().Contain("50"));
        cut.FindAll(".pg-databases tbody tr")[1].TextContent.Should().Contain("—");
        cut.Find(".pg-databases-footnote").TextContent.Should().Contain("not audited");
    }

    [Fact]
    public void Unreachable_with_a_snapshot_shows_the_error_stale_tiles_and_skeleton_rows()
    {
        Arrange(OverviewTestData.Overview(captured: T0));
        RenderPage();
        Time.Advance(TimeSpan.FromMinutes(48));
        ArrangeFailure();
        var cut = RenderPage();

        cut.Find(".pg-overview-error").TextContent.Should().Contain("Can't reach pg-prod-eu.");
        cut.Find(".pg-overview-retry-in").TextContent.Should().Be("retrying in 30 s");
        cut.Find(".pg-tile-connections").ClassList.Should().Contain("pg-stale");
        cut.Find(".pg-tile-connections").TextContent.Should().Contain("stale · 12:00");
        cut.Find(".pg-attention").TextContent.Should().Contain("waiting for connection");
        cut.FindAll(".pg-attention .pg-attention-item").Should().HaveCount(3);
        cut.FindAll(".pg-attention .mud-skeleton").Should().NotBeEmpty();
        cut.FindAll(".pg-databases-card .mud-skeleton").Should().NotBeEmpty();
    }

    [Fact]
    public void Unreachable_without_a_snapshot_shows_skeleton_tiles_and_the_error()
    {
        ArrangeFailure();
        var cut = RenderPage();

        cut.Find(".pg-overview-error").Should().NotBeNull();
        cut.FindAll(".pg-tiles .pg-tile").Should().HaveCount(5);
        cut.FindAll(".pg-tiles .mud-skeleton").Should().NotBeEmpty();
        cut.FindAll(".pg-tile-connections").Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_now_calls_the_handler_again_and_recovery_clears_the_error()
    {
        ArrangeFailure();
        var cut = RenderPage();
        await Operations.Received(1).GetOverviewAsync(Secret, Arg.Any<CancellationToken>());

        Arrange(OverviewTestData.Overview());
        cut.Find(".pg-overview-retry").Click();

        cut.WaitForAssertion(() => cut.FindAll(".pg-overview-error").Should().BeEmpty());
        await Operations.Received(2).GetOverviewAsync(Secret, Arg.Any<CancellationToken>());
        cut.FindAll(".pg-tile-connections").Should().HaveCount(1);
    }

    [Fact]
    public async Task Countdown_ticks_each_second_and_reloads_at_zero()
    {
        ArrangeFailure();
        var cut = RenderPage();

        Time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => cut.Find(".pg-overview-retry-in").TextContent.Should().Be("retrying in 25 s"));
        await Operations.Received(1).GetOverviewAsync(Secret, Arg.Any<CancellationToken>());

        Arrange(OverviewTestData.Overview());
        Time.Advance(TimeSpan.FromSeconds(25));
        cut.WaitForAssertion(() => Operations.Received(2).GetOverviewAsync(Secret, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".pg-overview-error").Should().BeEmpty();
            cut.Find(".pg-tile-connections").TextContent.Should().Contain("/ 100 max_connections");
        });
    }

    [Fact]
    public async Task A_load_completing_after_dispose_does_not_start_a_retry_timer()
    {
        var gate = new TaskCompletionSource<PgOverview>();
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var cut = Render<SbConsole.Plugins.Postgres.Pages.Overview>();
        cut.WaitForState(() => Operations.ReceivedCalls().Any(), TimeSpan.FromSeconds(5));

        await DisposeComponentsAsync();
        gate.SetException(new TimeoutException("late"));
        await Task.Delay(100);
        Time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100);

        await Operations.Received(1).GetOverviewAsync(Secret, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_connection_result_does_not_auto_retry()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        var cut = RenderPage();

        cut.FindAll(".pg-overview-retry-in").Should().BeEmpty();
        Time.Advance(TimeSpan.FromSeconds(90));
        await Task.Delay(100);
        await Operations.DidNotReceiveWithAnyArgs().GetOverviewAsync(default!, default);
    }

    [Theory]
    [InlineData(4, "0.4")]
    [InlineData(12346, "1,235")]
    public void Commits_per_second_shows_one_decimal_under_ten(long delta, string expected)
    {
        Arrange(OverviewTestData.Overview(captured: T0, commits: 100));
        var cut = RenderPage();
        Time.Advance(TimeSpan.FromSeconds(10));
        Arrange(OverviewTestData.Overview(captured: T0.AddSeconds(10), commits: 100 + delta));
        cut.Find(".pg-overview-refresh").Click();

        cut.WaitForAssertion(() => cut.Find(".pg-databases tbody tr").TextContent.Should().Contain(expected));
    }

    [Fact]
    public void No_connections_shows_no_skeleton_tiles()
    {
        Connections.ListAsync("postgres", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SbConsole.Sdk.ConnectionInfo>>([]));
        var cut = Render<SbConsole.Plugins.Postgres.Pages.Overview>();
        cut.WaitForState(() => cut.FindAll(".pg-no-connections").Count > 0, TimeSpan.FromSeconds(5));

        cut.FindAll(".mud-skeleton").Should().BeEmpty();
        cut.FindAll(".pg-tiles").Should().BeEmpty();
    }
}
