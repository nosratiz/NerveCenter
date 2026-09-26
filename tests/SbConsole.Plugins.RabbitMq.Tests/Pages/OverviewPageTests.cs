using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Tests.Components;
using OverviewPage = SbConsole.Plugins.RabbitMq.Pages.Overview;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class OverviewPageTests : RabbitPageTestBase
{
    private const long Gib = 1024L * 1024 * 1024;

    private static BrokerOverview Broker(double? publish = 1840.4, double? deliver = 1006, double? ack = 998, double? unroutable = 37) =>
        new("rabbit@uk-prod", "3.13.7", "26.2", publish, deliver, ack, unroutable,
            Connections: 214, Channels: 618, Queues: 12, Exchanges: 20, Consumers: 30,
            MessagesReady: 4354, MessagesUnacked: 12, ConnectionsOpened: 1284, ConnectionsClosed: 1190, ChannelsOpened: 3602);

    private static NodeSummary Node(string name, long memUsed, bool memAlarm = false, bool diskAlarm = false, bool running = true, long diskFree = 180 * Gib) =>
        new(name, running, memUsed, 6400L * 1024 * 1024, memAlarm, diskFree, 50L * 1024 * 1024, diskAlarm, 4102, 65536, TimeSpan.FromDays(3));

    private static QueueSummary Queue(string name, long ready, long unacked = 0) => new(
        "/orders", name, "classic", true, false, false, ready, unacked, 1, null, null, null, null, null, null, null, null, false, null, null, 0, null, null,
        new Dictionary<string, object?>(), new Dictionary<string, object?>());

    private void GivenSnapshot(BrokerOverview? broker = null, IReadOnlyList<NodeSummary>? nodes = null)
    {
        Operations.GetOverviewAsync(DevSecret, Arg.Any<CancellationToken>()).Returns(broker ?? Broker());
        Operations.ListNodesAsync(DevSecret, Arg.Any<CancellationToken>()).Returns(nodes ?? new List<NodeSummary>
        {
            Node("rabbit@node-1", 3113851290),
            Node("rabbit@node-2", 6300L * 1024 * 1024),
            Node("rabbit@node-3", 3543348019),
        });
        Operations.ListQueuesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>()).Returns(new List<QueueSummary>
        {
            Queue("shipment.updates", 88),
            Queue("payments-dlq", 214),
            Queue("billing.retry", 1940, 5),
            Queue("idle.q", 0),
            Queue("order-events.q", 1204, 30),
            Queue("audit.sink", 908),
            Queue("small.q", 3),
        });
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync()
    {
        RenderFragment page = builder =>
        {
            builder.OpenComponent<OverviewPage>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    private static OverviewPage Page(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindComponent<OverviewPage>().Instance;

    [Fact]
    public async Task Renders_header_tiles_nodes_deepest_queues_and_churn()
    {
        GivenSnapshot();

        var cut = await RenderPageAsync();

        cut.Find(".overview-header").TextContent.Should().Contain("3 nodes · cluster rabbit@uk-prod · 3.13.7");
        cut.Find(".refreshed-ago").TextContent.Should().Contain("Refreshed 0s ago");
        cut.Find(".tile-publish .tile-value").TextContent.Should().Be("1,840/s");
        cut.Find(".tile-publish .tile-caption").TextContent.Should().Contain("4,354 ready");
        cut.Find(".tile-deliver .tile-value").TextContent.Should().Be("1,006/s");
        cut.Find(".tile-deliver .tile-caption").TextContent.Should().Contain("ack 998/s");
        cut.Find(".tile-connections .tile-value").TextContent.Should().Be("214");
        cut.Find(".tile-connections .tile-caption").TextContent.Should().Contain("618 channels");
        cut.Find(".tile-unrouted .tile-value").TextContent.Should().Be("37/s");
        cut.Find(".tile-unrouted .tile-caption").TextContent.Should().Contain("dropped or returned");

        var nodeRows = cut.FindAll(".node-row");
        nodeRows.Should().HaveCount(3);
        nodeRows[0].TextContent.Should().Contain("rabbit@node-1").And.Contain("2.9G").And.Contain("180G").And.Contain("4,102 / 65,536").And.Contain("Running");

        var deepest = cut.FindAll(".deepest-queue");
        deepest.Select(d => d.QuerySelector(".deepest-queue-link")!.TextContent).Should().Equal(
            "billing.retry", "order-events.q", "audit.sink", "payments-dlq", "shipment.updates");
        deepest[0].QuerySelector(".queue-ready")!.TextContent.Should().Be("1,940");
        deepest[0].QuerySelector(".queue-unacked")!.TextContent.Should().Be("5");
        deepest[0].QuerySelector(".deepest-queue-link")!.GetAttribute("href").Should()
            .Be($"/p/rabbitmq/queues/billing.retry?connectionId={Dev.Id}&vhost=%2Forders");

        cut.Find(".churn-connections-opened").TextContent.Should().Be("1,284");
        cut.Find(".churn-connections-closed").TextContent.Should().Be("1,190");
        cut.Find(".churn-channels-opened").TextContent.Should().Be("3,602");
    }

    [Fact]
    public async Task Deliver_below_publish_is_flagged()
    {
        GivenSnapshot();

        var cut = await RenderPageAsync();

        cut.Find(".tile-deliver").ClassList.Should().Contain("below-publish");
        cut.Find(".tile-deliver .tile-value").ClassList.Should().Contain("mud-warning-text");
    }

    [Fact]
    public async Task Unrouted_zero_reads_none()
    {
        GivenSnapshot(Broker(unroutable: 0, deliver: 2000));

        var cut = await RenderPageAsync();

        cut.Find(".tile-unrouted .tile-caption").TextContent.Should().Contain("none");
        cut.Find(".tile-deliver").ClassList.Should().NotContain("below-publish");
    }

    [Fact]
    public async Task A_node_in_memory_alarm_gets_a_banner_and_an_alarm_chip()
    {
        GivenSnapshot(nodes: [Node("rabbit@node-1", 1 * Gib), Node("rabbit@node-2", 6 * Gib, memAlarm: true), Node("rabbit@node-3", 1 * Gib, diskAlarm: true, diskFree: 40L * 1024 * 1024)]);

        var cut = await RenderPageAsync();

        var alarms = cut.FindAll(".node-alarm").Select(a => a.TextContent).ToList();
        alarms.Should().HaveCount(2);
        alarms[0].Should().Contain("Memory alarm on rabbit@node-2 — publishers are blocked while the alarm holds.");
        alarms[1].Should().Contain("Disk alarm on rabbit@node-3");
        var rows = cut.FindAll(".node-row");
        rows[1].QuerySelector(".node-state")!.TextContent.Should().Contain("Mem alarm");
        rows[2].QuerySelector(".node-state")!.TextContent.Should().Contain("Disk alarm");
    }

    [Fact]
    public async Task A_stopped_node_reads_down()
    {
        GivenSnapshot(nodes: [Node("rabbit@node-1", 1 * Gib, running: false)]);

        var cut = await RenderPageAsync();

        cut.Find(".overview-header").TextContent.Should().Contain("1 node ·");
        cut.Find(".node-row .node-state").TextContent.Should().Contain("Down");
    }

    [Fact]
    public async Task Null_rates_render_as_an_em_dash()
    {
        GivenSnapshot(Broker(publish: null, deliver: null, ack: null, unroutable: null));

        var cut = await RenderPageAsync();

        cut.Find(".tile-publish .tile-value").TextContent.Should().Be("—");
        cut.Find(".tile-deliver .tile-value").TextContent.Should().Be("—");
        cut.Find(".tile-deliver .tile-caption").TextContent.Should().Contain("ack —");
        cut.Find(".tile-unrouted .tile-value").TextContent.Should().Be("—");
    }

    [Fact]
    public async Task First_load_failure_shows_the_management_error_and_retry_recovers()
    {
        GivenSnapshot();
        Operations.GetOverviewAsync(DevSecret, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "GET", "/api/overview", "Access refused."));

        var cut = await RenderPageAsync();

        cut.Find(".management-error").TextContent.Should().Contain("403");
        cut.FindAll(".tile-publish").Should().BeEmpty();

        Operations.GetOverviewAsync(DevSecret, Arg.Any<CancellationToken>()).Returns(Broker());
        await cut.Find(".retry-load").ClickAsync(new());
        await SettleAsync(cut);

        cut.FindAll(".management-error").Should().BeEmpty();
        cut.Find(".tile-publish .tile-value").TextContent.Should().Be("1,840/s");
    }

    [Fact]
    public async Task Refresh_failure_keeps_the_last_good_data_dimmed_under_a_stale_banner()
    {
        GivenSnapshot();
        var cut = await RenderPageAsync();

        Clock.Now = Clock.Now.AddMinutes(3);
        Operations.GetOverviewAsync(DevSecret, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(503, "GET", "/api/overview", "Service Unavailable"));
        await cut.InvokeAsync(() => Page(cut).AutoRefreshTickAsync());
        cut.Render();

        var banner = cut.Find(".stale-banner");
        banner.TextContent.Should().Contain("Refresh failed —").And.Contain("Showing values from 14:03, 3m ago.");
        cut.Find(".stale-data").GetAttribute("style").Should().Contain("opacity:.55");
        cut.Find(".stale-data .tile-publish .tile-value").TextContent.Should().Be("1,840/s");
        cut.FindAll(".management-error").Should().BeEmpty();

        await cut.Find(".stale-banner .toggle-auto-refresh").ClickAsync(new());
        Page(cut).IsAutoRefreshing.Should().BeFalse();
        cut.Find(".stale-banner .toggle-auto-refresh").TextContent.Should().Be("Resume auto-refresh");
    }

    [Fact]
    public async Task AutoRefreshTick_reloads_and_updates_the_refreshed_caption()
    {
        GivenSnapshot();
        var cut = await RenderPageAsync();
        Page(cut).IsAutoRefreshing.Should().BeTrue();

        Operations.GetOverviewAsync(DevSecret, Arg.Any<CancellationToken>()).Returns(Broker(publish: 2500));
        Clock.Now = Clock.Now.AddSeconds(10);
        var reloaded = await cut.InvokeAsync(() => Page(cut).AutoRefreshTickAsync());
        cut.Render();

        reloaded.Should().BeTrue();
        cut.Find(".tile-publish .tile-value").TextContent.Should().Be("2,500/s");
        await Operations.Received(2).GetOverviewAsync(DevSecret, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pause_stops_auto_refresh_and_resume_restarts_it()
    {
        GivenSnapshot();
        var cut = await RenderPageAsync();

        await cut.Find(".auto-refresh-toggle").ClickAsync(new());
        Page(cut).IsAutoRefreshing.Should().BeFalse();
        cut.Find(".auto-refresh-toggle").TextContent.Should().Contain("Resume");

        await cut.Find(".auto-refresh-toggle").ClickAsync(new());
        Page(cut).IsAutoRefreshing.Should().BeTrue();
    }

    [Fact]
    public async Task A_load_for_a_superseded_vhost_is_discarded()
    {
        GivenSnapshot();
        var slow = new TaskCompletionSource<IReadOnlyList<QueueSummary>>();
        Operations.ListQueuesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>()).Returns(slow.Task);
        Operations.ListQueuesAsync(DevSecret, "/", Arg.Any<CancellationToken>()).Returns(new List<QueueSummary> { Queue("root.q", 5) });
        var cut = await RenderPageAsync();

        await OpenMenuAsync(cut, "vhost-menu");
        await cut.FindAll(".vhost-option").Single(o => o.TextContent.Contains("(default)")).ClickAsync(new());
        await SettleAsync(cut);
        slow.SetResult(new List<QueueSummary> { Queue("orders.q", 9) });
        await SettleAsync(cut);

        cut.FindAll(".deepest-queue-link").Select(l => l.TextContent).Should().Equal("root.q");
    }

    [Fact]
    public async Task No_connections_shows_only_the_empty_state()
    {
        ConnectionsProvider.ListAsync("rabbitmq", Arg.Any<CancellationToken>()).Returns(new List<SbConsole.Sdk.ConnectionInfo>());

        var cut = await RenderPageAsync();

        cut.Markup.Should().Contain("No connections yet.");
        await Operations.DidNotReceiveWithAnyArgs().GetOverviewAsync(default!, default);
    }
}
