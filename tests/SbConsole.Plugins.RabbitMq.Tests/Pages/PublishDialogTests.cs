using System.Text;
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

public class PublishDialogTests : RabbitPageTestBase
{
    public PublishDialogTests()
    {
        SeededTopology.Seed(Operations, DevSecret);
        SeededTopology.Seed(Operations, ProdSecret);
        Operations.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(PublishOutcome.Routed);
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderAsync(
        string exchange, string routingKey = "", bool prod = false, bool passExchanges = true)
    {
        var connection = prod ? Prod : Dev;
        var cut = RenderDialog<PublishDialog>(new Dictionary<string, object?>
        {
            [nameof(PublishDialog.ConnectionId)] = connection.Id,
            [nameof(PublishDialog.ConnectionName)] = connection.Name,
            [nameof(PublishDialog.IsProd)] = prod,
            [nameof(PublishDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(PublishDialog.Exchange)] = exchange,
            [nameof(PublishDialog.RoutingKey)] = routingKey,
            [nameof(PublishDialog.Exchanges)] = passExchanges ? SeededTopology.Exchanges() : null,
        });
        await SettleAsync(cut);
        return cut;
    }

    private static Task TypeRoutingKeyAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string key) =>
        TypeAsync(cut, ".routing-key input", key);

    // The routing key and headers fields are debounced (the preview recomputes as you type), so
    // wait past the debounce before asserting.
    private static async Task TypeAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string selector, string value)
    {
        await cut.Find(selector).InputAsync(new ChangeEventArgs { Value = value });
        await Task.Delay(250);
        cut.Render();
    }

    private static bool PublishDisabled(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.Find("button.publish-submit").HasAttribute("disabled");

    [Fact]
    public async Task A_topic_key_previews_every_matching_queue_with_its_binding_key()
    {
        var cut = await RenderAsync("order-events");
        await TypeRoutingKeyAsync(cut, "order.uk.created");

        cut.Find(".exchange-type-chip").TextContent.Should().Contain("topic");
        cut.Find(".route-summary").TextContent.Should().Contain("Will route to 2 queues");
        var rows = cut.FindAll(".route-row").Select(r => (r.QuerySelector(".route-destination")!.TextContent, r.QuerySelector(".route-key")!.TextContent)).ToList();
        rows.Should().Equal(("order-events.q", "order.*.created"), ("audit.sink", "#"));
        PublishDisabled(cut).Should().BeFalse();
        cut.FindAll(".unroutable-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_that_matches_nothing_blocks_publish_and_lists_the_closest_bindings()
    {
        // Only the order.* bindings: drop the catch-all so nothing matches.
        Operations.ListBindingsAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>())
            .Returns(SeededTopology.Bindings().Where(b => b.RoutingKey != "#").ToList());
        var cut = await RenderAsync("order-events");
        await TypeRoutingKeyAsync(cut, "order.created");

        var panel = cut.Find(".unroutable-panel");
        panel.TextContent.Should().Contain("Matches no binding")
            .And.Contain("This exchange has no alternate exchange, so the message would be dropped silently.")
            .And.Contain("Closest bindings");
        cut.FindAll(".closest-binding .closest-key").Select(k => k.TextContent).Should().Contain(["order.*.created", "order.*.amended"]);
        PublishDisabled(cut).Should().BeTrue();

        await cut.Find(".publish-anyway").ClickAsync(new());

        PublishDisabled(cut).Should().BeFalse();
    }

    [Fact]
    public async Task An_exchange_with_an_alternate_exchange_says_where_the_miss_goes_and_allows_publish()
    {
        var cut = await RenderAsync("shipment.updates");
        await TypeRoutingKeyAsync(cut, "shipment.eu.late");

        cut.Find(".route-alternate").TextContent.Should().Contain("Would go to alternate exchange unrouted.ae");
        cut.FindAll(".unroutable-panel").Should().BeEmpty();
        PublishDisabled(cut).Should().BeFalse();
    }

    [Fact]
    public async Task The_default_exchange_routes_by_queue_name()
    {
        var cut = await RenderAsync("", routingKey: "billing.retry");

        cut.Find(".route-summary").TextContent.Should().Contain("Will route to 1 queue");
        cut.Find(".route-destination").TextContent.Should().Be("billing.retry");
    }

    [Fact]
    public async Task A_headers_exchange_previews_from_the_headers_textarea()
    {
        var cut = await RenderAsync("invoice.headers");
        cut.Find(".unroutable-panel").Should().NotBeNull();

        await TypeAsync(cut, ".headers-input textarea", "region: eu");

        cut.Find(".route-summary").TextContent.Should().Contain("Will route to 1 queue");
        cut.Find(".route-key").TextContent.Should().Contain("region=eu");
    }

    [Fact]
    public async Task Bad_header_lines_show_a_validation_message_and_disable_publish()
    {
        var cut = await RenderAsync("notify.fanout");

        await TypeAsync(cut, ".headers-input textarea", "not a header");

        cut.Find(".headers-errors").TextContent.Should().Contain("Line 1");
        PublishDisabled(cut).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_bindings_read_makes_the_preview_unavailable_but_allows_publish()
    {
        Operations.ListBindingsAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "GET", "/api/bindings/%2Forders", "access refused"));

        var cut = await RenderAsync("order-events");
        await TypeRoutingKeyAsync(cut, "order.created");

        cut.Find(".preview-unavailable").TextContent.Should().Contain("Routing preview unavailable —")
            .And.Contain("The broker will still return an unroutable message.");
        PublishDisabled(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Without_an_exchange_list_the_dialog_reads_one_itself()
    {
        var cut = await RenderAsync("order-events", passExchanges: false);
        await TypeRoutingKeyAsync(cut, "order.uk.created");

        await Operations.Received(1).ListExchangesAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>());
        cut.Find(".route-summary").TextContent.Should().Contain("Will route to 2 queues");
    }

    [Fact]
    public async Task Routed_publishes_the_request_shows_a_snackbar_and_closes()
    {
        var cut = await RenderAsync("order-events");
        await TypeRoutingKeyAsync(cut, "order.uk.created");
        await TypeAsync(cut, ".headers-input textarea", "x-source: sbconsole");
        await TypeAsync(cut, ".body-input textarea", "{\"orderId\":\"ord_41908\"}");

        await cut.Find("button.publish-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).PublishAsync(DevSecret, SeededTopology.Vhost, Arg.Is<PublishRequest>(r =>
            r.Exchange == "order-events" && r.RoutingKey == "order.uk.created" && r.Persistent && r.ContentType == "application/json"
            && Encoding.UTF8.GetString(r.Body) == "{\"orderId\":\"ord_41908\"}"
            && r.Headers != null && r.Headers["x-source"] == "sbconsole"), Arg.Any<CancellationToken>());
        await Confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default);
        Services.GetRequiredService<ISnackbar>().ShownSnackbars.Should().ContainSingle(s => s.Message == "Published — routed to 2 queues");
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task An_unroutable_result_keeps_the_dialog_open_with_the_reason()
    {
        Operations.PublishAsync(DevSecret, SeededTopology.Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(PublishOutcome.Unroutable);
        var cut = await RenderAsync("order-events");
        await TypeRoutingKeyAsync(cut, "order.uk.created");

        await cut.Find("button.publish-submit").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".publish-error").TextContent.Should().Contain("Broker returned the message: unroutable");
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default!);
    }

    [Fact]
    public async Task A_failed_publish_shows_the_error_inline()
    {
        Operations.PublishAsync(DevSecret, SeededTopology.Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cut = await RenderAsync("notify.fanout");

        await cut.Find("button.publish-submit").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".publish-error").TextContent.Should().NotBeEmpty();
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default!);
    }

    [Fact]
    public async Task On_prod_the_publish_is_confirmed_first_and_a_refusal_publishes_nothing()
    {
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderAsync("notify.fanout", prod: true);

        await cut.Find("button.publish-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Publish to", "notify.fanout", true, null, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default!, default);

        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(true);
        await cut.Find("button.publish-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).PublishAsync(ProdSecret, SeededTopology.Vhost, Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }
}
