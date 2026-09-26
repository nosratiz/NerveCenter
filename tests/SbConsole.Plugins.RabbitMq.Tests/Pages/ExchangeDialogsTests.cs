using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Pages;
using SbConsole.Plugins.RabbitMq.Tests.Components;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class ExchangeDialogsTests : RabbitPageTestBase
{
    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderCreateAsync()
    {
        var cut = RenderDialog<CreateExchangeDialog>(new Dictionary<string, object?>
        {
            [nameof(CreateExchangeDialog.ConnectionId)] = Dev.Id,
            [nameof(CreateExchangeDialog.ConnectionName)] = Dev.Name,
            [nameof(CreateExchangeDialog.Vhost)] = SeededTopology.Vhost,
        });
        await SettleAsync(cut);
        return cut;
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderBindingsAsync(string exchange, string? alternate = null)
    {
        var cut = RenderDialog<ExchangeBindingsDialog>(new Dictionary<string, object?>
        {
            [nameof(ExchangeBindingsDialog.ConnectionId)] = Dev.Id,
            [nameof(ExchangeBindingsDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(ExchangeBindingsDialog.Exchange)] = exchange,
            [nameof(ExchangeBindingsDialog.AlternateExchange)] = alternate,
        });
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Create_requires_a_name_and_rejects_the_reserved_amq_prefix()
    {
        var cut = await RenderCreateAsync();
        cut.Find("button.create-exchange-submit").HasAttribute("disabled").Should().BeTrue();

        await cut.Find(".exchange-name input").InputAsync(new ChangeEventArgs { Value = "amq.mine" });
        cut.Find("button.create-exchange-submit").HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Names starting with amq. are reserved");

        await cut.Find(".exchange-name input").InputAsync(new ChangeEventArgs { Value = "payments.events" });
        cut.Find("button.create-exchange-submit").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Create_submits_the_request_with_defaults_and_closes()
    {
        var cut = await RenderCreateAsync();
        await cut.Find(".exchange-name input").InputAsync(new ChangeEventArgs { Value = "payments.events" });
        await cut.Find(".alternate-exchange input").InputAsync(new ChangeEventArgs { Value = "unrouted.ae" });
        await cut.Find(".internal-toggle input").ChangeAsync(new ChangeEventArgs { Value = true });

        await cut.Find("button.create-exchange-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).CreateExchangeAsync(DevSecret, SeededTopology.Vhost,
            new CreateExchangeRequest("payments.events", "direct", Durable: true, AutoDelete: false, Internal: true, AlternateExchange: "unrouted.ae"),
            Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task A_failed_create_shows_the_error_inline_and_stays_open()
    {
        Operations.CreateExchangeAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CreateExchangeRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(406, "PUT", "/api/exchanges/%2Forders/payments.events", "inequivalent arg 'type'"));
        var cut = await RenderCreateAsync();
        await cut.Find(".exchange-name input").InputAsync(new ChangeEventArgs { Value = "payments.events" });

        await cut.Find("button.create-exchange-submit").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".create-exchange-error").TextContent.Should().NotBeEmpty();
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default!);
    }

    [Fact]
    public async Task Bindings_dialog_lists_destination_type_key_and_arguments()
    {
        SeededTopology.Seed(Operations, DevSecret);
        Operations.ListBindingsAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CancellationToken>()).Returns(
            SeededTopology.Bindings().Append(SeededTopology.Binding("invoice.headers", "audit.fanout", "", destinationType: "exchange")).ToList());

        var cut = await RenderBindingsAsync("invoice.headers");

        var rows = cut.FindAll(".binding-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".binding-destination")!.TextContent.Should().Be("invoices.eu");
        rows[0].QuerySelector(".binding-type")!.TextContent.Should().Be("queue");
        rows[0].QuerySelector(".binding-key")!.TextContent.Should().Be("(no key)");
        rows[0].QuerySelector(".binding-args")!.TextContent.Should().Be("x-match=all · region=eu");
        rows[1].QuerySelector(".binding-type")!.TextContent.Should().Be("exchange");
    }

    [Fact]
    public async Task Bindings_dialog_empty_state_says_messages_are_dropped()
    {
        SeededTopology.Seed(Operations, DevSecret);

        var plain = await RenderBindingsAsync("legacy.import");

        plain.Find(".no-exchange-bindings").TextContent.Should().Contain("No bindings — messages published here are dropped.");
    }

    [Fact]
    public async Task Bindings_dialog_empty_state_mentions_the_alternate_exchange_when_there_is_one()
    {
        SeededTopology.Seed(Operations, DevSecret);

        var withAe = await RenderBindingsAsync("shipment.updates", alternate: "unrouted.ae");
        withAe.Find(".no-exchange-bindings").TextContent.Should().Contain("dropped unless the alternate exchange unrouted.ae takes them.");
    }
}
