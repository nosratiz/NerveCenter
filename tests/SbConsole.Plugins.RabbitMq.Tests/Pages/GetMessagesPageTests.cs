using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Messages;
using SbConsole.Plugins.RabbitMq.Pages;
using SbConsole.Plugins.RabbitMq.Tests.Components;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class GetMessagesPageTests : RabbitPageTestBase
{
    private const string Vhost = SeededTopology.Vhost;
    private const string Queue = PaymentsDlq.Queue;

    private const string PeekByline = "Peek requeues every message. Nothing is removed, but each message comes back marked redelivered.";
    private const string ConsumeByline = "Consume acknowledges and removes every message it gets. Selected messages can be requeued or republished from this page until you leave it.";

    private readonly List<PublishRequest> _published = [];

    public GetMessagesPageTests()
    {
        var queue = SeededTopology.Queue(Queue) with { Ready = 214, Consumers = 0 };
        Operations.GetQueueAsync(DevSecret, Vhost, Queue, Arg.Any<CancellationToken>()).Returns(new QueueDetails(queue, [], []));
        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(new List<QueueSummary> { queue });
        Operations.ListBindingsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(new List<BindingInfo>());
        Operations.ListExchangesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(PaymentsDlq.Exchanges());
        Operations.GetMessagesAsync(DevSecret, Vhost, Queue, Arg.Any<int>(), Arg.Any<GetMode>(), Arg.Any<CancellationToken>())
            .Returns(PaymentsDlq.Messages());
        Operations.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<PublishRequest>(_published.Add), Arg.Any<CancellationToken>())
            .Returns(PublishOutcome.Routed);
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(true);
    }

    private ISnackbar Snackbar => Services.GetRequiredService<ISnackbar>();

    private string Query => $"connectionId={Dev.Id}&vhost=%2Forders";

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync(Guid? connectionId = null)
    {
        Navigation.NavigateTo($"/p/rabbitmq/queues/{Queue}/get?connectionId={connectionId ?? Dev.Id}&vhost={Uri.EscapeDataString(Vhost)}");
        RenderFragment page = builder =>
        {
            builder.OpenComponent<GetMessages>(0);
            builder.AddComponentParameter(1, nameof(GetMessages.Name), Uri.EscapeDataString(Queue));
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    private static Task SelectAsync<T>(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, T value)
    {
        var select = cut.FindComponents<MudSelect<T>>().Single(s => (s.Instance.Class ?? "").Split(' ').Contains(cssClass));
        return cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
    }

    private static async Task GetAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await cut.Find("button.get-submit").ClickAsync(new());
        await SettleAsync(cut);
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> ConsumeAsync()
    {
        var cut = await RenderPageAsync();
        await SelectAsync(cut, "get-mode", GetMode.Consume);
        await GetAsync(cut);
        return cut;
    }

    private static IReadOnlyList<IElement> Rows(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) => cut.FindAll(".message-row");

    private static async Task SelectRowAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, int row)
    {
        await Rows(cut)[row].QuerySelector(".message-select input")!.ChangeAsync(new ChangeEventArgs { Value = true });
        cut.Render();
    }

    private static string Squash(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Text(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string selector) => Squash(cut.Find(selector).TextContent);

    private static string Property(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string key) =>
        cut.FindAll(".property-row").Single(r => r.QuerySelector(".property-key")!.TextContent.Trim() == key).QuerySelector(".property-value")!.TextContent.Trim();

    [Fact]
    public void The_route_takes_the_escaped_queue_name_as_one_segment()
    {
        var route = typeof(GetMessages).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();
        route.Template.Should().Be("/p/rabbitmq/queues/{Name}/get");
    }

    [Fact]
    public async Task Peek_lists_the_messages_with_id_time_death_chip_and_routing_key()
    {
        var cut = await RenderPageAsync();
        cut.Find("a.breadcrumb-queues").GetAttribute("href").Should().Be($"/p/rabbitmq/queues?{Query}");
        cut.Find("a.breadcrumb-name").GetAttribute("href").Should().Be($"/p/rabbitmq/queues/{Queue}?{Query}");
        cut.FindAll(".prod-chip").Should().BeEmpty();

        await GetAsync(cut);

        await Operations.Received(1).GetMessagesAsync(DevSecret, Vhost, Queue, 25, GetMode.Peek, Arg.Any<CancellationToken>());
        await Confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default);
        Text(cut, ".result-header").Should().Be("4 of 214 shown · oldest first");
        var rows = Rows(cut);
        rows.Select(r => r.QuerySelector(".message-id")!.TextContent.Trim()).Should().Equal("pay_8814c2", "pay_8814bf", "pay_8813a0", "#4");
        rows.Select(r => r.QuerySelector(".message-time")!.TextContent.Trim()).Should().Equal("02:11:04", "02:10:58", "02:10:52", "02:10:46");
        rows.Select(r => r.QuerySelector(".death-chip")?.TextContent.Trim()).Should().Equal("rejected ×5", "rejected ×5", "expired", "maxlen");
        rows.Select(r => r.QuerySelector(".message-key")!.TextContent.Trim()).Should().Equal(
            "payment.capture.failed", "payment.capture.failed", "payment.capture.request", "audit.write");
        cut.FindAll(".held-warning").Should().BeEmpty("peeked messages are still in the queue");
    }

    [Fact]
    public async Task The_header_drops_the_depth_when_the_queue_read_fails()
    {
        Operations.GetQueueAsync(DevSecret, Vhost, Queue, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        var cut = await RenderPageAsync();

        await GetAsync(cut);

        Text(cut, ".result-header").Should().Be("4 shown · oldest first");
    }

    [Fact]
    public async Task The_byline_states_the_consequence_of_each_mode()
    {
        var cut = await RenderPageAsync();
        Text(cut, ".get-byline").Should().Be(PeekByline);

        await SelectAsync(cut, "get-mode", GetMode.Consume);

        Text(cut, ".get-byline").Should().Be(ConsumeByline);
    }

    [Fact]
    public async Task Consume_is_confirmed_first_and_cancel_gets_nothing()
    {
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderPageAsync();
        await SelectAsync(cut, "get-mode", GetMode.Consume);
        await SelectAsync(cut, "get-count", 10);

        await GetAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Consume", Queue, false, 10, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().GetMessagesAsync(default!, default!, default!, default, default, default);
        Rows(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task Consumed_messages_raise_the_held_warning()
    {
        var cut = await ConsumeAsync();

        await Operations.Received(1).GetMessagesAsync(DevSecret, Vhost, Queue, 25, GetMode.Consume, Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("rabbitmq.message.consume", Arg.Any<string>(), ActionRisk.Destructive, true, "4 consumed", Arg.Any<CancellationToken>());
        Text(cut, ".held-warning").Should().Be("4 consumed messages exist only on this page — requeue or republish them before leaving.");
    }

    [Fact]
    public async Task Requeue_republishes_to_the_default_exchange_keyed_by_the_queue_and_releases_the_messages()
    {
        var cut = await ConsumeAsync();
        await SelectRowAsync(cut, 0);
        await SelectRowAsync(cut, 2);
        Text(cut, ".selection-count").Should().Be("2 selected");

        await cut.Find("button.requeue-selected").ClickAsync(new());
        await SettleAsync(cut);

        _published.Should().HaveCount(2);
        _published.Should().AllSatisfy(r =>
        {
            r.Exchange.Should().Be("");
            r.RoutingKey.Should().Be(Queue);
        });
        _published.Select(r => r.MessageId).Should().Equal("pay_8814c2", "pay_8813a0");
        await Audit.Received(1).RecordAsync(PublishMessageCommandHandler.Action, Arg.Any<string>(), ActionRisk.Mutating, true,
            "requeue: 2 routed, 0 unroutable", Arg.Any<CancellationToken>());
        Rows(cut).Select(r => r.QuerySelector(".message-id")!.TextContent.Trim()).Should().Equal("pay_8814bf", "#4");
        Text(cut, ".held-warning").Should().StartWith("2 consumed messages exist only on this page");
        cut.FindAll(".selection-bar").Should().BeEmpty();
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "requeue: 2 routed, 0 unroutable" && s.Severity == Severity.Success);
    }

    [Fact]
    public async Task Requeuing_everything_clears_the_held_warning()
    {
        var cut = await ConsumeAsync();
        for (var i = 0; i < 4; i++)
        {
            await SelectRowAsync(cut, i);
        }

        await cut.Find("button.requeue-selected").ClickAsync(new());
        await SettleAsync(cut);

        cut.FindAll(".held-warning").Should().BeEmpty();
        Rows(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unroutable_requeue_warns_and_keeps_the_messages()
    {
        Operations.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(PublishOutcome.Unroutable);
        var cut = await ConsumeAsync();
        await SelectRowAsync(cut, 0);

        await cut.Find("button.requeue-selected").ClickAsync(new());
        await SettleAsync(cut);

        Snackbar.ShownSnackbars.Should().Contain(s => s.Message!.StartsWith("requeue: 0 routed, 1 unroutable", StringComparison.Ordinal) && s.Severity == Severity.Warning);
        Rows(cut).Should().HaveCount(4);
    }

    [Fact]
    public async Task Requeue_is_disabled_for_peeked_messages()
    {
        var cut = await RenderPageAsync();
        await GetAsync(cut);
        await SelectRowAsync(cut, 0);

        cut.Find("button.requeue-selected").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button.republish-selected").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Republish_opens_the_dialog_as_a_copy_in_peek_mode()
    {
        var dialogs = Substitute.For<IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync();
        await GetAsync(cut);
        await SelectRowAsync(cut, 1);

        await cut.Find("button.republish-selected").ClickAsync(new());

        await dialogs.Received(1).ShowAsync<RepublishDialog>(
            Arg.Any<string>(),
            Arg.Is<DialogParameters>(p =>
                (bool)p[nameof(RepublishDialog.IsCopy)]! == true
                && (string)p[nameof(RepublishDialog.SourceQueue)]! == Queue
                && ((IReadOnlyList<RabbitMessage>)p[nameof(RepublishDialog.Messages)]!).Single().MessageId == "pay_8814bf"
                && ((IReadOnlyList<ExchangeSummary>)p[nameof(RepublishDialog.Exchanges)]!).Any(e => e.Name == PaymentsDlq.SourceExchange)),
            Arg.Any<DialogOptions>());
    }

    [Fact]
    public async Task A_consumed_republish_releases_the_messages_the_dialog_republished()
    {
        var dialogs = Substitute.For<IDialogService>();
        var reference = Substitute.For<IDialogReference>();
        reference.Result.Returns(DialogResult.Ok(new RepublishResult(1, 0)));
        dialogs.ShowAsync<RepublishDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>()).Returns(reference);
        Services.AddSingleton(dialogs);
        var cut = await ConsumeAsync();
        await SelectRowAsync(cut, 0);

        await cut.Find("button.republish-selected").ClickAsync(new());
        await SettleAsync(cut);

        await dialogs.Received(1).ShowAsync<RepublishDialog>(Arg.Any<string>(),
            Arg.Is<DialogParameters>(p => (bool)p[nameof(RepublishDialog.IsCopy)]! == false), Arg.Any<DialogOptions>());
        Rows(cut).Should().HaveCount(3);
        Snackbar.ShownSnackbars.Should().Contain(s => s.Message == "republish: 1 routed, 0 unroutable");
    }

    [Fact]
    public async Task The_republish_shortcut_sends_the_focused_message_back_to_its_first_death_exchange()
    {
        var cut = await RenderPageAsync();
        await GetAsync(cut);
        await Rows(cut)[2].ClickAsync(new());

        Text(cut, "button.republish-first-death").Should().Be($"Republish to {PaymentsDlq.SourceExchange}");
        await cut.Find("button.republish-first-death").ClickAsync(new());
        await SettleAsync(cut);

        _published.Should().ContainSingle();
        _published[0].Exchange.Should().Be(PaymentsDlq.SourceExchange);
        _published[0].RoutingKey.Should().Be("payment.capture.request");
        _published[0].MessageId.Should().Be("pay_8813a0");
        Rows(cut).Should().HaveCount(4, "a peeked message is still in the queue; only a copy was published");
    }

    [Fact]
    public async Task The_detail_pane_shows_death_history_properties_and_a_pretty_body()
    {
        var cut = await RenderPageAsync();
        await GetAsync(cut);

        var detail = cut.Find(".message-detail");
        Squash(detail.QuerySelector(".detail-id")!.TextContent).Should().Be("pay_8814c2");
        var death = cut.FindAll(".death-row").Single();
        new[] { ".death-exchange", ".death-reason", ".death-keys", ".death-count" }
            .Select(c => death.QuerySelector(c)!.TextContent.Trim())
            .Should().Equal("billing.direct", "rejected", "payment.capture.failed", "×5 · 02:11:04");
        Text(cut, ".body-meta").Should().Be("application/json · 86B");
        cut.Find(".message-body").TextContent.Should().Contain("{\n  \"paymentId\": \"pay_8814c2\",\n  \"orderId\": \"ord_41908\",\n  \"amount\": {\n    \"value\": 149.00,");
        Property(cut, "message_id").Should().Be("pay_8814c2");
        Property(cut, "correlation_id").Should().Be("ord_41908");
        Property(cut, "delivery_mode").Should().Be("2 · persistent");
        Property(cut, "priority").Should().Be("0");
        Property(cut, "app_id").Should().Be("billing-svc");
        Property(cut, "timestamp").Should().Be("2026-09-26 02:11:04");
        Property(cut, "x-death count").Should().Be("5");
        Property(cut, "x-first-death-queue").Should().Be(PaymentsDlq.SourceQueue);
        Property(cut, "tenant").Should().Be("uk");
        Property(cut, "redelivered").Should().Be("false (before this peek)");
    }

    [Fact]
    public async Task Text_bodies_are_shown_as_is()
    {
        var cut = await RenderPageAsync();
        await GetAsync(cut);

        await Rows(cut)[3].ClickAsync(new());

        cut.Find(".message-body").TextContent.Should().Be("plain text");
        cut.FindAll(".death-row").Single().TextContent.Should().Contain("maxlen");
    }

    [Fact]
    public async Task Copy_writes_the_body_to_the_clipboard()
    {
        var cut = await RenderPageAsync();
        await GetAsync(cut);

        await cut.Find("button.copy-body").ClickAsync(new());

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "navigator.clipboard.writeText" && (string)i.Arguments[0]! == PaymentsDlq.Json);
    }

    [Fact]
    public async Task An_empty_queue_offers_get_again_and_back_to_queues()
    {
        Operations.GetMessagesAsync(DevSecret, Vhost, Queue, Arg.Any<int>(), Arg.Any<GetMode>(), Arg.Any<CancellationToken>())
            .Returns(new List<RabbitMessage>());
        var cut = await RenderPageAsync();

        await GetAsync(cut);

        Text(cut, ".get-empty .empty-title").Should().Be("Queue is empty");
        cut.Find(".get-empty a.back-to-queues").GetAttribute("href").Should().Be($"/p/rabbitmq/queues?{Query}");
        await cut.Find(".get-empty button.get-again").ClickAsync(new());
        await SettleAsync(cut);
        await Operations.Received(2).GetMessagesAsync(DevSecret, Vhost, Queue, 25, GetMode.Peek, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_exclusive_consumer_shows_the_locked_state()
    {
        Operations.GetMessagesAsync(DevSecret, Vhost, Queue, Arg.Any<int>(), Arg.Any<GetMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationInterruptedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 405, "RESOURCE_LOCKED", 60, 70)));
        var cut = await RenderPageAsync();

        await GetAsync(cut);

        Text(cut, ".get-locked .locked-text").Should().Be(
            $"Cannot get from {Queue} — the queue has an exclusive consumer. Peeking would compete with the live consumer.");
        cut.Find(".get-locked a.view-consumers").GetAttribute("href").Should().Be($"/p/rabbitmq/queues/{Queue}?{Query}");
        await cut.Find(".get-locked button.retry-get").ClickAsync(new());
        await SettleAsync(cut);
        await Operations.Received(2).GetMessagesAsync(DevSecret, Vhost, Queue, 25, GetMode.Peek, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Other_errors_use_the_management_error_alert_with_retry()
    {
        Operations.GetMessagesAsync(DevSecret, Vhost, Queue, Arg.Any<int>(), Arg.Any<GetMode>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException());
        var cut = await RenderPageAsync();

        await GetAsync(cut);

        Text(cut, ".management-error-text").Should().Be("Timed out talking to the broker");
        cut.FindAll(".get-locked").Should().BeEmpty();
        await cut.Find("button.retry-load").ClickAsync(new());
        await SettleAsync(cut);
        await Operations.Received(2).GetMessagesAsync(DevSecret, Vhost, Queue, 25, GetMode.Peek, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_while_loading_cancels_the_get()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<RabbitMessage>>();
        CancellationToken token = default;
        Operations.GetMessagesAsync(DevSecret, Vhost, Queue, Arg.Any<int>(), Arg.Any<GetMode>(), Arg.Do<CancellationToken>(t => token = t))
            .Returns(pending.Task);
        var cut = await RenderPageAsync();

        _ = cut.Find("button.get-submit").ClickAsync(new());
        await SettleAsync(cut);
        Text(cut, ".get-loading").Should().Contain("Getting 25 messages… Messages are held unacked until the page returns them. Cancelling requeues immediately.");

        await cut.Find(".get-loading button.cancel-get").ClickAsync(new());
        token.IsCancellationRequested.Should().BeTrue();
        pending.SetException(new OperationCanceledException(token));
        await SettleAsync(cut);

        cut.FindAll(".get-loading").Should().BeEmpty();
        cut.FindAll(".management-error").Should().BeEmpty("a cancelled get is not an error");
        Rows(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_connection_says_so()
    {
        var cut = await RenderPageAsync(Guid.NewGuid());

        cut.Find(".connection-not-found").TextContent.Should().Contain("Connection not found.");
        cut.FindAll("button.get-submit").Should().BeEmpty();
    }

    [Fact]
    public async Task A_prod_connection_shows_the_prod_chip()
    {
        Operations.GetQueueAsync(ProdSecret, Vhost, Queue, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var cut = await RenderPageAsync(Prod.Id);

        cut.FindAll(".prod-chip").Should().ContainSingle();
    }
}
