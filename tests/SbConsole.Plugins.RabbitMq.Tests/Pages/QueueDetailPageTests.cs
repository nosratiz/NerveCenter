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

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class QueueDetailPageTests : RabbitPageTestBase
{
    private const string Vhost = SeededTopology.Vhost;
    private const string Queue = "order-events.q";

    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    // The mockup's order-events.q: bound from the order-events topic exchange twice and from a
    // fanout, dead-lettering by argument through billing.retry.dlx to payments-dlq, with an
    // orders-limits policy that also sets max-length (the declared argument wins) and fills in an
    // overflow the queue never declared.
    private static QueueSummary OrderEvents() =>
        SeededTopology.Queue(Queue, dlx: "billing.retry.dlx") with
        {
            Ready = 1204,
            Unacked = 18,
            Consumers = 6,
            AckRate = 980,
            MemoryBytes = 18 * 1024 * 1024,
            DeadLetterRoutingKey = "dlq.order-events",
            MaxLength = 500_000,
            Overflow = "reject-publish",
            Policy = "orders-limits",
            Arguments = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "billing.retry.dlx",
                ["x-dead-letter-routing-key"] = "dlq.order-events",
                ["x-max-length"] = 500_000L,
            },
            EffectivePolicyDefinition = new Dictionary<string, object?>
            {
                ["max-length"] = 100_000L,
                ["overflow"] = "reject-publish",
            },
        };

    private static List<QueueSummary> QueueList() =>
    [
        OrderEvents(),
        SeededTopology.Queue("payments-dlq") with { Ready = 214, Consumers = 0 },
        SeededTopology.Queue("billing.retry", dlx: "billing.x") with
        {
            Ready = 1940,
            Consumers = 0,
            Policy = "retry-shortttl",
            EffectivePolicyDefinition = new Dictionary<string, object?> { ["dead-letter-exchange"] = "billing.x", ["message-ttl"] = 30_000L },
        },
        SeededTopology.Queue("billing.payments.q") with { Ready = 7 },
        SeededTopology.Queue("notify.email"),
    ];

    private static List<BindingInfo> BindingList() =>
    [
        SeededTopology.Binding("order-events", Queue, "order.*.created"),
        SeededTopology.Binding("order-events", Queue, "order.*.amended"),
        SeededTopology.Binding("audit.fanout", Queue, ""),
        SeededTopology.Binding("", Queue, Queue),
        SeededTopology.Binding("billing.retry.dlx", "payments-dlq", "dlq.order-events"),
        SeededTopology.Binding("billing.x", "billing.payments.q", "billing.#"),
        SeededTopology.Binding("", "notify.email", "notify.email"),
    ];

    private static List<ExchangeSummary> ExchangeList() =>
    [
        .. SeededTopology.Exchanges(),
        SeededTopology.Exchange("audit.fanout", "fanout"),
        SeededTopology.Exchange("billing.x", "topic"),
    ];

    private static List<ConsumerInfo> ConsumerList(int count = 6) =>
        Enumerable.Range(0, count).Select(i => new ConsumerInfo($"order-svc-{i}", $"10.0.0.{i}:5672 -> 10.0.1.1:5672 (1)", i == 5 ? 0 : 32, true, false)).ToList();

    private void Seed(List<QueueSummary>? queues = null)
    {
        var all = queues ?? QueueList();
        var bindings = BindingList();
        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(all);
        Operations.ListBindingsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(bindings);
        Operations.ListExchangesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(ExchangeList());
        foreach (var q in all)
        {
            Operations.GetQueueAsync(DevSecret, Vhost, q.Name, Arg.Any<CancellationToken>())
                .Returns(new QueueDetails(q, q.Name == Queue ? ConsumerList() : [], bindings.Where(b => b.Destination == q.Name).ToList()));
        }
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync(string name = Queue, Guid? connectionId = null, string vhost = Vhost)
    {
        Navigation.NavigateTo($"/p/rabbitmq/queues/{Uri.EscapeDataString(name)}?connectionId={connectionId ?? Dev.Id}&vhost={Uri.EscapeDataString(vhost)}");
        RenderFragment page = builder =>
        {
            builder.OpenComponent<QueueDetail>(0);
            builder.AddComponentParameter(1, nameof(QueueDetail.Name), Uri.EscapeDataString(name));
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    private static string Text(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string selector) =>
        cut.Find(selector).TextContent.Trim();

    private static string Squash(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IReadOnlyList<IElement> BindingRows(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindAll(".binding-row");

    private static IElement ArgumentRow(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string key) =>
        cut.FindAll(".arguments .argument-row").Single(r => r.QuerySelector(".argument-key")!.TextContent.Trim() == key);

    private MudBlazor.ISnackbar Snackbar => Services.GetRequiredService<MudBlazor.ISnackbar>();

    private string Query => $"connectionId={Dev.Id}&vhost=%2Forders";

    [Fact]
    public void The_route_takes_the_escaped_queue_name_as_one_segment()
    {
        var route = typeof(QueueDetail).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();
        route.Template.Should().Be("/p/rabbitmq/queues/{Name}");
    }

    [Fact]
    public async Task Breadcrumb_actions_and_tiles_describe_the_queue()
    {
        Seed();

        var cut = await RenderPageAsync();

        cut.Find("a.breadcrumb-queues").GetAttribute("href").Should().Be($"/p/rabbitmq/queues?{Query}");
        Text(cut, ".breadcrumb-name").Should().Be(Queue);
        cut.Find("a.get-messages").GetAttribute("href").Should().Be($"/p/rabbitmq/queues/order-events.q/get?{Query}");

        Text(cut, ".tile-ready .tile-value").Should().Be("1,204");
        Text(cut, ".tile-unacked .tile-value").Should().Be("18");
        Text(cut, ".tile-consumers .tile-value").Should().Be("6");
        Text(cut, ".tile-ack .tile-value").Should().Be("980/s");
        Text(cut, ".tile-memory .tile-value").Should().Be("18M");
        cut.Markup.Should().NotContain("1,222", "ready and unacked are never summed");
        cut.FindAll(".dlq-chip").Should().BeEmpty();
    }

    [Fact]
    public async Task Routes_in_lists_the_bindings_without_the_implicit_default_exchange_one()
    {
        Seed();

        var cut = await RenderPageAsync();

        Text(cut, ".routes-in-title").Should().Be("Routes in — 3 bindings");
        var rows = BindingRows(cut);
        rows.Select(r => r.QuerySelector(".binding-exchange")!.TextContent.Trim()).Should().Equal("audit.fanout", "order-events", "order-events");
        rows.Select(r => r.QuerySelector(".binding-key")!.TextContent.Trim()).Should().Equal("(no key)", "order.*.amended", "order.*.created");
        rows[0].QuerySelector("a.binding-exchange")!.GetAttribute("href").Should().Be($"/p/rabbitmq/exchanges?{Query}");
        rows[0].QuerySelector(".binding-type")!.TextContent.Trim().Should().Be("fanout");
        rows[1].QuerySelector(".binding-type")!.ClassList.Should().Contain("mud-chip-filled", "topic chips are filled primary");
        rows[0].QuerySelector(".binding-type")!.ClassList.Should().NotContain("mud-chip-filled");
        cut.Find(".routes-in").TextContent.Should().Contain("Per-binding rates aren't shown — the management API has no per-binding statistics.");
    }

    [Fact]
    public async Task A_queue_with_only_the_default_binding_says_so()
    {
        Seed();

        var cut = await RenderPageAsync("notify.email");

        Text(cut, ".routes-in-title").Should().Be("Routes in — 0 bindings");
        BindingRows(cut).Should().BeEmpty();
        Squash(Text(cut, ".no-bindings")).Should().Be("No bindings — this queue only receives messages published to the default exchange with routing key notify.email.");
    }

    [Fact]
    public async Task Route_out_follows_the_dead_letter_argument_to_its_target_queue()
    {
        Seed();

        var cut = await RenderPageAsync();

        var routeOut = cut.Find(".route-out");
        Squash(routeOut.QuerySelector(".dead-letter-route")!.TextContent).Should().Be("Rejected or expired → billing.retry.dlx · dlq.order-events → payments-dlq · 214");
        routeOut.QuerySelector("a.dead-letter-target")!.GetAttribute("href").Should().Be($"/p/rabbitmq/queues/payments-dlq?{Query}");
        routeOut.QuerySelectorAll(".depends-on-key").Should().BeEmpty();
        Squash(routeOut.QuerySelector(".dead-letter-set-by")!.TextContent).Should().Be(
            "Set by x-dead-letter-exchange on the queue, so it is an argument rather than a binding — shown here anyway because it is where the messages actually go.");
    }

    [Fact]
    public async Task Route_out_set_by_a_policy_without_a_key_depends_on_the_message_routing_key()
    {
        Seed();

        var cut = await RenderPageAsync("billing.retry");

        var routeOut = cut.Find(".route-out");
        Squash(routeOut.QuerySelector(".dead-letter-route")!.TextContent).Should().StartWith("Rejected or expired → billing.x · original routing key → billing.payments.q · 7");
        routeOut.QuerySelector(".depends-on-key")!.TextContent.Should().Contain("depends on the message's routing key");
        Squash(routeOut.QuerySelector(".dead-letter-set-by")!.TextContent).Should().StartWith("Set by policy retry-shortttl");
    }

    [Fact]
    public async Task A_queue_without_a_dead_letter_exchange_drops_its_dead_letters()
    {
        Seed();

        var cut = await RenderPageAsync("notify.email");

        Text(cut, ".route-out .no-dead-letter").Should().Be("No dead-letter route — rejected and expired messages are dropped.");
    }

    [Fact]
    public async Task A_dead_letter_queue_is_flagged()
    {
        Seed();

        var cut = await RenderPageAsync("payments-dlq");

        Text(cut, ".dlq-chip").Should().Be("dead-letter queue");
    }

    [Fact]
    public async Task Arguments_show_declared_values_and_name_the_policy_beside_the_keys_it_governs()
    {
        Seed();

        var cut = await RenderPageAsync();

        ArgumentRow(cut, "durable").QuerySelector(".argument-value")!.TextContent.Trim().Should().Be("true");
        ArgumentRow(cut, "x-queue-type").QuerySelector(".argument-value")!.TextContent.Trim().Should().Be("classic");
        ArgumentRow(cut, "x-dead-letter-exchange").QuerySelector(".argument-value")!.TextContent.Trim().Should().Be("billing.retry.dlx");

        var maxLength = ArgumentRow(cut, "x-max-length");
        maxLength.QuerySelector(".argument-value")!.TextContent.Trim().Should().Be("500,000");
        maxLength.QuerySelector(".argument-wins")!.TextContent.Trim().Should().Be("argument wins over policy orders-limits");
        maxLength.QuerySelector(".policy-override").Should().BeNull();

        var overflow = ArgumentRow(cut, "x-overflow");
        overflow.QuerySelector(".argument-value")!.TextContent.Trim().Should().Be("reject-publish");
        overflow.QuerySelector(".policy-override")!.TextContent.Trim().Should().Be("policy orders-limits");
    }

    [Fact]
    public async Task Consumers_show_the_first_three_and_expand_the_rest()
    {
        Seed();

        var cut = await RenderPageAsync();

        cut.FindAll(".consumers .consumer-row").Should().HaveCount(3);
        cut.Find(".consumers .consumer-row").TextContent.Should().Contain("order-svc-0").And.Contain("prefetch 32");
        await cut.Find(".consumers .more-consumers").ClickAsync(new());

        cut.FindAll(".consumers .consumer-row").Should().HaveCount(6);
        cut.FindAll(".consumers .consumer-row")[5].TextContent.Should().Contain("unlimited");
    }

    [Fact]
    public async Task A_queue_without_consumers_says_so()
    {
        Seed();

        var cut = await RenderPageAsync("payments-dlq");

        Text(cut, ".consumers .no-consumers").Should().Be("No consumers.");
    }

    [Fact]
    public async Task Unbind_confirms_then_removes_the_binding_by_its_properties_key_and_reloads()
    {
        Seed();
        Confirmation.ConfirmAsync("Unbind", "order-events → order-events.q", false, null, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync();

        var amended = BindingRows(cut).Single(r => r.QuerySelector(".binding-key")!.TextContent.Trim() == "order.*.amended");
        await amended.QuerySelector(".unbind")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).RemoveBindingAsync(DevSecret, Vhost, "order-events", Queue, "order.*.amended", Arg.Any<CancellationToken>());
        await Operations.Received(2).GetQueueAsync(DevSecret, Vhost, Queue, Arg.Any<CancellationToken>());
        Snackbar.ShownSnackbars.Should().Contain(s => s.Severity == Severity.Success);
    }

    [Fact]
    public async Task Cancelled_unbind_does_nothing()
    {
        Seed();
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderPageAsync();

        await BindingRows(cut)[0].QuerySelector(".unbind")!.ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Unbind", "audit.fanout → order-events.q", false, null, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().RemoveBindingAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Purge_confirms_with_the_ready_count_then_purges_and_reloads()
    {
        Seed();
        Confirmation.ConfirmAsync("Purge", Queue, false, 1204, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync();

        await cut.Find(".purge-queue").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).PurgeQueueAsync(DevSecret, Vhost, Queue, Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("rabbitmq.queue.purge", Arg.Any<string>(), SbConsole.Sdk.ActionRisk.Destructive, true, "1,204 ready", Arg.Any<CancellationToken>());
        await Operations.Received(2).GetQueueAsync(DevSecret, Vhost, Queue, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_on_a_prod_connection_passes_is_prod_from_the_server_side_lookup()
    {
        Operations.ListQueuesAsync(ProdSecret, Vhost, Arg.Any<CancellationToken>()).Returns(QueueList());
        Operations.ListBindingsAsync(ProdSecret, Vhost, Arg.Any<CancellationToken>()).Returns(BindingList());
        Operations.ListExchangesAsync(ProdSecret, Vhost, Arg.Any<CancellationToken>()).Returns(ExchangeList());
        Operations.GetQueueAsync(ProdSecret, Vhost, Queue, Arg.Any<CancellationToken>()).Returns(new QueueDetails(OrderEvents(), [], []));
        var cut = await RenderPageAsync(connectionId: Prod.Id);

        cut.FindAll(".prod-chip").Should().HaveCount(1);
        await cut.Find(".purge-queue").ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Purge", Queue, true, 1204, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().PurgeQueueAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Add_binding_opens_the_dialog_with_the_bindable_exchanges()
    {
        Seed();
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync();

        await cut.Find(".add-binding").ClickAsync(new());

        await dialogs.Received(1).ShowAsync<AddBindingDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p =>
                (string)p[nameof(AddBindingDialog.Queue)]! == Queue
                && (string)p[nameof(AddBindingDialog.Vhost)]! == Vhost
                && (Guid)p[nameof(AddBindingDialog.ConnectionId)]! == Dev.Id
                && ((IReadOnlyList<ExchangeSummary>)p[nameof(AddBindingDialog.Exchanges)]!).Any(e => e.Name == "order-events")
                && !((IReadOnlyList<ExchangeSummary>)p[nameof(AddBindingDialog.Exchanges)]!).Any(e => e.IsDefault)),
            Arg.Any<DialogOptions>());
    }

    [Fact]
    public async Task Publish_opens_the_publish_dialog_on_the_default_exchange_keyed_by_the_queue()
    {
        Seed();
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync();

        await cut.Find(".publish-queue").ClickAsync(new());

        await dialogs.Received(1).ShowAsync<PublishDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p =>
                (string)p[nameof(PublishDialog.Exchange)]! == ""
                && (string)p[nameof(PublishDialog.RoutingKey)]! == Queue
                && (bool)p[nameof(PublishDialog.IsProd)]! == false),
            Arg.Any<DialogOptions>());
    }

    [Fact]
    public async Task A_missing_queue_says_it_was_not_found_with_a_way_back()
    {
        Seed();
        Operations.GetQueueAsync(DevSecret, Vhost, "gone.q", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(404, "GET", "/api/queues/%2Forders/gone.q", "Object Not Found"));

        var cut = await RenderPageAsync("gone.q");

        Text(cut, ".queue-not-found .not-found-text").Should().Be("Queue gone.q not found in /orders");
        cut.Find(".queue-not-found a.back-to-queues").GetAttribute("href").Should().Be($"/p/rabbitmq/queues?{Query}");
        cut.FindAll(".management-error").Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_connection_says_so_with_a_link_back()
    {
        Seed();

        var cut = await RenderPageAsync(connectionId: Guid.NewGuid());

        cut.Find(".connection-not-found").TextContent.Should().Contain("Connection not found");
        cut.Find(".connection-not-found a").GetAttribute("href").Should().Be("/p/rabbitmq/queues");
        await Operations.DidNotReceiveWithAnyArgs().GetQueueAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_first_load_failure_shows_the_management_error_and_retry_recovers()
    {
        Seed();
        Operations.GetQueueAsync(DevSecret, Vhost, Queue, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "GET", "/api/queues/%2Forders/order-events.q", "Access refused."));

        var cut = await RenderPageAsync();

        cut.Find(".management-error").TextContent.Should().Contain("403");
        Seed();
        await cut.Find(".retry-load").ClickAsync(new());
        await SettleAsync(cut);

        cut.FindAll(".management-error").Should().BeEmpty();
        Text(cut, ".tile-ready .tile-value").Should().Be("1,204");
    }

    [Fact]
    public async Task Loading_shows_skeletons()
    {
        Seed();
        var slow = new TaskCompletionSource<QueueDetails>();
        Operations.GetQueueAsync(DevSecret, Vhost, Queue, Arg.Any<CancellationToken>()).Returns(slow.Task);

        var cut = await RenderPageAsync();

        cut.FindAll(".queue-detail-loading").Should().HaveCount(1);
        slow.SetResult(new QueueDetails(OrderEvents(), [], []));
        await SettleAsync(cut);
        cut.FindAll(".queue-detail-loading").Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_data_dimmed_under_a_stale_banner()
    {
        Seed();
        var cut = await RenderPageAsync();

        Clock.Now = Clock.Now.AddMinutes(2);
        Operations.GetQueueAsync(DevSecret, Vhost, Queue, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(504, "GET", "/api/queues/%2Forders/order-events.q", "Gateway Timeout"));
        await cut.Find(".refresh-detail").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".stale-banner").TextContent.Should().Contain("Refresh failed —").And.Contain("Showing values from 14:03, 2m ago.");
        cut.FindAll(".stale-banner .toggle-auto-refresh").Should().BeEmpty();
        cut.Find(".stale-data").GetAttribute("style").Should().Contain("opacity:.55");
        Text(cut, ".stale-data .tile-ready .tile-value").Should().Be("1,204");
        cut.FindAll(".management-error").Should().BeEmpty();
    }
}
