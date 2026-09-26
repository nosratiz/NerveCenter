using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Pages;
using SbConsole.Plugins.RabbitMq.Tests.Components;
using QueuesPage = SbConsole.Plugins.RabbitMq.Pages.Queues;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class QueuesPageTests : RabbitPageTestBase
{
    private const string Vhost = SeededTopology.Vhost;

    // The mockup's /orders queues: a DLX'd work queue, a TTL retry queue nobody consumes, the one
    // terminal DLQ, a quorum queue redelivering a poison message, a policy-governed queue, a
    // length-capped queue, a lazy queue and one idle for two weeks.
    private List<QueueSummary> QueueList() =>
    [
        SeededTopology.Queue("order-events.q", dlx: "orders.dlx") with { Ready = 1204, Unacked = 18, Consumers = 6, AckRate = 980, RedeliverRate = 0 },
        SeededTopology.Queue("billing.retry") with { Ready = 1940, Consumers = 0, AckRate = 0, RedeliverRate = 0, MessageTtlMs = 30_000 },
        SeededTopology.Queue("payments-dlq") with { Ready = 214, Consumers = 0, AckRate = 0, RedeliverRate = 0 },
        SeededTopology.Queue("audit.sink") with { Type = "quorum", ReplicaCount = 3, Ready = 908, Unacked = 190, Consumers = 2, AckRate = 712, RedeliverRate = 41 },
        SeededTopology.Queue("shipment.updates.q") with { Ready = 88, Unacked = 4, Consumers = 3, AckRate = 96, Policy = "ha-orders" },
        SeededTopology.Queue("invoice.issued.q") with { Unacked = 2, Consumers = 2, AckRate = 12, MaxLength = 500_000 },
        SeededTopology.Queue("notify.sms.q") with { Consumers = 1, AckRate = 96, Lazy = true },
        SeededTopology.Queue("legacy.import.q") with { Consumers = 0, Durable = false, IdleSince = Clock.Now.AddDays(-14) },
    ];

    private void Seed(List<QueueSummary>? queues = null)
    {
        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(queues ?? QueueList());
        Operations.ListBindingsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(new List<BindingInfo>
        {
            SeededTopology.Binding("orders.dlx", "payments-dlq", "#"),
            SeededTopology.Binding("order-events", "order-events.q", "order.*.created"),
        });
        Operations.ListExchangesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(new List<ExchangeSummary>
        {
            SeededTopology.Exchange("", "direct"),
            SeededTopology.Exchange("order-events", "topic"),
            SeededTopology.Exchange("orders.dlx", "topic"),
        });
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync()
    {
        RenderFragment page = builder =>
        {
            builder.OpenComponent<QueuesPage>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    private static QueuesPage Page(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindComponent<QueuesPage>().Instance;

    private static IReadOnlyList<string> Names(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindAll(".queue-row .queue-name").Select(e => e.TextContent.Trim()).ToList();

    private static IElement Row(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".queue-row").Single(r => r.QuerySelector(".queue-name")!.TextContent.Trim() == name);

    private static string Text(IElement row, string selector) => row.QuerySelector(selector)!.TextContent.Trim();

    private MudBlazor.ISnackbar Snackbar => Services.GetRequiredService<MudBlazor.ISnackbar>();

    [Fact]
    public async Task Lists_queues_with_ready_and_unacked_as_separate_numbers_and_a_summary()
    {
        Seed();

        var cut = await RenderPageAsync();

        Names(cut).Should().HaveCount(8);
        cut.Find(".queues-summary").TextContent.Should().Contain("8 queues · 4,354 ready · 214 unacked");

        var orders = Row(cut, "order-events.q");
        Text(orders, ".queue-ready").Should().Be("1,204");
        Text(orders, ".queue-unacked").Should().Be("18");
        Text(orders, ".queue-consumers").Should().Be("6");
        Text(orders, ".rate-ack").Should().Be("980/s");
        Text(orders, ".rate-redeliver").Should().Be("0/s");
        orders.TextContent.Should().NotContain("1,222", "ready and unacked are never summed");

        var link = orders.QuerySelector("a.queue-link")!;
        link.GetAttribute("href").Should().Be($"/p/rabbitmq/queues/order-events.q?connectionId={Dev.Id}&vhost=%2Forders");
        orders.QuerySelector("a.get-queue")!.GetAttribute("href").Should().Be($"/p/rabbitmq/queues/order-events.q/get?connectionId={Dev.Id}&vhost=%2Forders");

        cut.Find(".redeliver-header").GetAttribute("title").Should()
            .Be("The management API has no per-queue nack rate; redeliveries are the poison-message signal.");
    }

    [Fact]
    public async Task Null_rates_render_as_a_dash()
    {
        Seed([SeededTopology.Queue("fresh.q")]);

        var cut = await RenderPageAsync();

        Text(Row(cut, "fresh.q"), ".rate-ack").Should().Be("—");
        Text(Row(cut, "fresh.q"), ".rate-redeliver").Should().Be("—");
    }

    [Fact]
    public async Task A_single_node_quorum_queue_reads_one_replica()
    {
        Seed([SeededTopology.Queue("audit.sink") with { Type = "quorum", ReplicaCount = 1 }]);

        var cut = await RenderPageAsync();

        Text(Row(cut, "audit.sink"), ".attr-quorum").Should().Be("quorum · 1 replica");
    }

    [Fact]
    public async Task Attribute_chips_describe_each_queue()
    {
        Seed();

        var cut = await RenderPageAsync();

        Text(Row(cut, "audit.sink"), ".attr-quorum").Should().Be("quorum · 3 replicas");
        Row(cut, "audit.sink").QuerySelector(".attr-durable").Should().BeNull("a quorum queue is always durable");
        Row(cut, "order-events.q").QuerySelector(".attr-durable").Should().NotBeNull();
        Row(cut, "legacy.import.q").QuerySelector(".attr-durable").Should().BeNull();

        Row(cut, "order-events.q").QuerySelector(".attr-dlx").Should().NotBeNull();
        cut.FindAll(".queue-row .attr-dlx").Should().HaveCount(1);

        Row(cut, "payments-dlq").QuerySelector(".attr-dlq").Should().NotBeNull();
        cut.FindAll(".queue-row .attr-dlq").Should().HaveCount(1, "only payments-dlq is a terminal dead-letter target");

        Text(Row(cut, "billing.retry"), ".attr-ttl").Should().Be("TTL 30s");
        Text(Row(cut, "shipment.updates.q"), ".attr-policy").Should().Be("policy ha-orders");
        Text(Row(cut, "invoice.issued.q"), ".attr-max").Should().Be("max 500,000");
        Row(cut, "notify.sms.q").QuerySelector(".attr-lazy").Should().NotBeNull();
        Text(Row(cut, "legacy.import.q"), ".attr-idle").Should().Be("idle 14d");
        cut.FindAll(".queue-row .attr-idle").Should().HaveCount(1);
    }

    [Fact]
    public async Task A_stream_queue_gets_a_stream_chip()
    {
        Seed([SeededTopology.Queue("events.stream") with { Type = "stream" }]);

        var cut = await RenderPageAsync();

        Row(cut, "events.stream").QuerySelector(".attr-stream").Should().NotBeNull();
    }

    [Fact]
    public async Task Warnings_flag_unconsumed_backlogs_and_redelivery()
    {
        Seed();

        var cut = await RenderPageAsync();

        Row(cut, "billing.retry").QuerySelector(".warn-no-consumers").Should().NotBeNull();
        Row(cut, "payments-dlq").QuerySelector(".warn-no-consumers").Should().BeNull("a DLQ is meant to sit unconsumed");
        Row(cut, "legacy.import.q").QuerySelector(".warn-no-consumers").Should().BeNull("nothing is waiting");
        cut.FindAll(".queue-row .warn-no-consumers").Should().HaveCount(1);

        Row(cut, "audit.sink").QuerySelector(".warn-redelivering").Should().NotBeNull();
        cut.FindAll(".queue-row .warn-redelivering").Should().HaveCount(1);
    }

    [Fact]
    public async Task Filter_matches_a_substring_case_insensitively()
    {
        Seed();
        var cut = await RenderPageAsync();

        await cut.Find(".queue-filter input").InputAsync(new ChangeEventArgs { Value = "NOTIFY" });

        Names(cut).Should().Equal("notify.sms.q");
        cut.Find(".queues-summary").TextContent.Should().Contain("8 queues", "the summary describes the vhost, not the filter");
    }

    [Fact]
    public async Task A_filter_matching_nothing_suggests_the_closest_name_and_can_be_cleared()
    {
        Seed();
        var cut = await RenderPageAsync();

        await cut.Find(".queue-filter input").InputAsync(new ChangeEventArgs { Value = "shipping." });

        cut.FindAll(".queue-row").Should().BeEmpty();
        var noMatch = cut.Find(".no-match");
        noMatch.TextContent.Should().Contain("No queue matches").And.Contain("shipping.").And.Contain("8 queues in this vhost.");
        cut.Find(".did-you-mean").TextContent.Trim().Should().Be("shipment.updates.q");

        await cut.Find(".did-you-mean").ClickAsync(new());
        Names(cut).Should().Equal("shipment.updates.q");

        await cut.Find(".queue-filter input").InputAsync(new ChangeEventArgs { Value = "zzzzzzzzzzzzzzzz" });
        cut.FindAll(".did-you-mean").Should().BeEmpty();
        await cut.Find(".clear-filter").ClickAsync(new());
        Names(cut).Should().HaveCount(8);
    }

    [Fact]
    public async Task An_empty_vhost_says_so()
    {
        Seed([]);

        var cut = await RenderPageAsync();

        cut.Find(".queues-empty").TextContent.Should().Contain("No queues in /orders.");
        cut.FindAll(".no-match").Should().BeEmpty();
    }

    [Fact]
    public async Task Purge_confirms_with_the_ready_count_then_purges_and_reloads()
    {
        Seed();
        Confirmation.ConfirmAsync("Purge", "billing.retry", false, 1940, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync();

        await Row(cut, "billing.retry").QuerySelector(".purge-queue")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).PurgeQueueAsync(DevSecret, Vhost, "billing.retry", Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("rabbitmq.queue.purge", Arg.Any<string>(), SbConsole.Sdk.ActionRisk.Destructive, true, "1,940 ready", Arg.Any<CancellationToken>());
        await Operations.Received(2).ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>());
        Snackbar.ShownSnackbars.Should().ContainSingle(s => s.Severity == Severity.Success && s.Message == "Purged billing.retry");
    }

    [Fact]
    public async Task Cancelled_purge_does_nothing()
    {
        Seed();
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderPageAsync();

        await Row(cut, "billing.retry").QuerySelector(".purge-queue")!.ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Purge", "billing.retry", false, 1940, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().PurgeQueueAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_failed_purge_shows_an_error_snackbar()
    {
        Seed();
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(true);
        Operations.PurgeQueueAsync(DevSecret, Vhost, "billing.retry", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "DELETE", "/api/queues/%2Forders/billing.retry/contents", "access refused"));
        var cut = await RenderPageAsync();

        await Row(cut, "billing.retry").QuerySelector(".purge-queue")!.ClickAsync(new());
        await SettleAsync(cut);

        Snackbar.ShownSnackbars.Should().ContainSingle(s => s.Severity == Severity.Error);
    }

    [Fact]
    public async Task Delete_from_the_overflow_menu_confirms_then_deletes()
    {
        Seed();
        Confirmation.ConfirmAsync("Delete", "legacy.import.q", false, null, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync();

        await Row(cut, "legacy.import.q").QuerySelector(".queue-menu button")!.ClickAsync(new());
        await SettleAsync(cut);
        await cut.Find(".delete-queue").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).DeleteQueueAsync(DevSecret, Vhost, "legacy.import.q", Arg.Any<CancellationToken>());
        await Operations.Received(2).ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_delete_does_nothing()
    {
        Seed();
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderPageAsync();

        await Row(cut, "legacy.import.q").QuerySelector(".queue-menu button")!.ClickAsync(new());
        await SettleAsync(cut);
        await cut.Find(".delete-queue").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().DeleteQueueAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Publish_opens_the_publish_dialog_on_the_default_exchange_keyed_by_the_queue()
    {
        Seed();
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync();

        await Row(cut, "billing.retry").QuerySelector(".publish-queue")!.ClickAsync(new());

        await dialogs.Received(1).ShowAsync<PublishDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p =>
                (string)p[nameof(PublishDialog.Exchange)]! == ""
                && (string)p[nameof(PublishDialog.RoutingKey)]! == "billing.retry"
                && (string)p[nameof(PublishDialog.Vhost)]! == Vhost
                && (Guid)p[nameof(PublishDialog.ConnectionId)]! == Dev.Id
                && p[nameof(PublishDialog.Exchanges)] != null),
            Arg.Any<DialogOptions>());
    }

    [Fact]
    public async Task Create_opens_the_create_queue_dialog_with_the_exchange_names()
    {
        Seed();
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync();

        await cut.Find(".create-queue").ClickAsync(new());

        await dialogs.Received(1).ShowAsync<CreateQueueDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p =>
                (string)p[nameof(CreateQueueDialog.Vhost)]! == Vhost
                && ((IReadOnlyList<string>)p[nameof(CreateQueueDialog.ExchangeNames)]!).Contains("orders.dlx")),
            Arg.Any<DialogOptions>());
    }

    [Fact]
    public async Task Auto_refresh_defaults_to_10s_when_nothing_is_stored()
    {
        Seed();
        Store.GetAsync("queues.autoRefreshSeconds", Arg.Any<CancellationToken>()).Returns((string?)null);

        var cut = await RenderPageAsync();

        Page(cut).AutoRefreshSeconds.Should().Be(10);
        Page(cut).IsAutoRefreshing.Should().BeTrue();
        cut.Find(".auto-refresh-10").ClassList.Should().Contain("mud-button-filled");
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", 0)]
    [InlineData("7", 10)]
    [InlineData("soon", 10)]
    public async Task Auto_refresh_reads_the_stored_choice_and_falls_back_to_10s(string stored, int expected)
    {
        Seed();
        Store.GetAsync("queues.autoRefreshSeconds", Arg.Any<CancellationToken>()).Returns(stored);

        var cut = await RenderPageAsync();

        Page(cut).AutoRefreshSeconds.Should().Be(expected);
        Page(cut).IsAutoRefreshing.Should().Be(expected != 0);
    }

    [Fact]
    public async Task An_unreadable_setting_falls_back_to_10s()
    {
        Seed();
        Store.GetAsync("queues.autoRefreshSeconds", Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("db locked"));

        var cut = await RenderPageAsync();

        Page(cut).AutoRefreshSeconds.Should().Be(10);
        Names(cut).Should().HaveCount(8);
    }

    [Fact]
    public async Task Choosing_an_interval_persists_it_and_off_stops_the_loop()
    {
        Seed();
        var cut = await RenderPageAsync();

        await cut.Find(".auto-refresh-off").ClickAsync(new());

        Page(cut).AutoRefreshSeconds.Should().Be(0);
        Page(cut).IsAutoRefreshing.Should().BeFalse();
        await Store.Received(1).SetAsync("queues.autoRefreshSeconds", "0", Arg.Any<CancellationToken>());

        await cut.Find(".auto-refresh-60").ClickAsync(new());
        Page(cut).IsAutoRefreshing.Should().BeTrue();
        await Store.Received(1).SetAsync("queues.autoRefreshSeconds", "60", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoRefreshTick_reloads_the_queues()
    {
        Seed();
        var cut = await RenderPageAsync();

        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { SeededTopology.Queue("billing.retry") with { Ready = 5 } });
        var reloaded = await cut.InvokeAsync(() => Page(cut).AutoRefreshTickAsync());
        cut.Render();

        reloaded.Should().BeTrue();
        Names(cut).Should().Equal("billing.retry");
        Text(Row(cut, "billing.retry"), ".queue-ready").Should().Be("5");
    }

    [Fact]
    public async Task Refresh_failures_keep_the_rows_dimmed_under_a_stale_banner_with_one_snackbar_per_streak()
    {
        Seed();
        var cut = await RenderPageAsync();

        Clock.Now = Clock.Now.AddMinutes(3);
        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(504, "GET", "/api/queues/%2Forders", "Gateway Timeout"));
        await cut.InvokeAsync(() => Page(cut).AutoRefreshTickAsync());
        await cut.InvokeAsync(() => Page(cut).AutoRefreshTickAsync());
        cut.Render();

        cut.Find(".stale-banner").TextContent.Should().Contain("Refresh failed —").And.Contain("Showing values from 14:03, 3m ago.");
        cut.Find(".stale-data").GetAttribute("style").Should().Contain("opacity:.55");
        cut.FindAll(".stale-data .queue-row").Should().HaveCount(8);
        cut.FindAll(".management-error").Should().BeEmpty();
        Snackbar.ShownSnackbars.Should().ContainSingle(s => s.Severity == Severity.Error);

        await cut.Find(".stale-banner .toggle-auto-refresh").ClickAsync(new());
        Page(cut).IsAutoRefreshing.Should().BeFalse();
    }

    [Fact]
    public async Task First_load_failure_shows_the_management_error_and_retry_recovers()
    {
        Seed();
        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "GET", "/api/queues/%2Forders", "Access refused."));

        var cut = await RenderPageAsync();

        cut.Find(".management-error").TextContent.Should().Contain("403");
        cut.FindAll(".queue-row").Should().BeEmpty();

        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(QueueList());
        await cut.Find(".retry-load").ClickAsync(new());
        await SettleAsync(cut);

        cut.FindAll(".management-error").Should().BeEmpty();
        Names(cut).Should().HaveCount(8);
    }

    [Fact]
    public async Task Loading_shows_skeleton_rows_and_the_rates_caption()
    {
        Seed();
        var slow = new TaskCompletionSource<IReadOnlyList<QueueSummary>>();
        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(slow.Task);

        var cut = await RenderPageAsync();

        cut.Find(".queues-loading").TextContent.Should().Contain("Rates need two samples — first values appear after 5s.");
        slow.SetResult(QueueList());
        await SettleAsync(cut);
        cut.FindAll(".queues-loading").Should().BeEmpty();
    }
}
