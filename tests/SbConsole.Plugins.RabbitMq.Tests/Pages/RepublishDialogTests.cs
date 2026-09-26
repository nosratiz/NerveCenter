using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Messages;
using SbConsole.Plugins.RabbitMq.Pages;
using SbConsole.Plugins.RabbitMq.Tests.Components;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class RepublishDialogTests : RabbitPageTestBase
{
    private readonly List<PublishRequest> _published = [];

    public RepublishDialogTests()
    {
        Operations.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<PublishRequest>(_published.Add), Arg.Any<CancellationToken>())
            .Returns(PublishOutcome.Routed);
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderAsync(
        IReadOnlyList<RabbitMessage> messages, bool isCopy = false, bool prod = false)
    {
        var connection = prod ? Prod : Dev;
        var cut = RenderDialog<RepublishDialog>(new Dictionary<string, object?>
        {
            [nameof(RepublishDialog.ConnectionId)] = connection.Id,
            [nameof(RepublishDialog.ConnectionName)] = connection.Name,
            [nameof(RepublishDialog.IsProd)] = prod,
            [nameof(RepublishDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(RepublishDialog.SourceQueue)] = PaymentsDlq.Queue,
            [nameof(RepublishDialog.Messages)] = messages,
            [nameof(RepublishDialog.IsCopy)] = isCopy,
            [nameof(RepublishDialog.Exchanges)] = PaymentsDlq.Exchanges(),
        });
        await SettleAsync(cut);
        return cut;
    }

    private static string SelectedExchange(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.Find(".republish-exchange input").GetAttribute("value")!;

    private static string RoutingKey(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.Find(".republish-routing-key input").GetAttribute("value")!;

    private static bool KeepOriginal(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        ((AngleSharp.Html.Dom.IHtmlInputElement)cut.Find(".keep-original-keys input")).IsChecked;

    private static async Task SubmitAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await cut.Find("button.republish-submit").ClickAsync(new());
        await SettleAsync(cut);
    }

    [Fact]
    public async Task Defaults_come_from_the_first_messages_first_death()
    {
        var messages = PaymentsDlq.Messages().Take(2).ToList();

        var cut = await RenderAsync(messages);

        SelectedExchange(cut).Should().Be(PaymentsDlq.SourceExchange);
        RoutingKey(cut).Should().Be("payment.capture.failed");
        KeepOriginal(cut).Should().BeFalse("both messages share one routing key");
        cut.FindAll(".copy-warning").Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_publishes_every_message_with_label_republish_and_returns_the_result()
    {
        var messages = PaymentsDlq.Messages().Take(2).ToList();
        var cut = await RenderAsync(messages);

        await SubmitAsync(cut);

        _published.Should().HaveCount(2);
        _published.Should().AllSatisfy(r =>
        {
            r.Exchange.Should().Be(PaymentsDlq.SourceExchange);
            r.RoutingKey.Should().Be("payment.capture.failed");
        });
        _published.Select(r => r.MessageId).Should().Equal("pay_8814c2", "pay_8814bf");
        await Audit.Received(1).RecordAsync(PublishMessageCommandHandler.Action, Arg.Any<string>(), ActionRisk.Mutating, true,
            "republish: 2 routed, 0 unroutable", Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r.Data is RepublishResult && ((RepublishResult)r.Data).Routed == 2));
        await Confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default);
    }

    [Fact]
    public async Task Differing_keys_default_to_keeping_each_messages_original_key()
    {
        var messages = PaymentsDlq.Messages().Take(3).ToList();
        var cut = await RenderAsync(messages);

        KeepOriginal(cut).Should().BeTrue();
        await SubmitAsync(cut);

        _published.Select(r => r.RoutingKey).Should().Equal("payment.capture.failed", "payment.capture.failed", "payment.capture.request");
        _published.Should().AllSatisfy(r => r.Exchange.Should().Be(PaymentsDlq.SourceExchange));
    }

    [Fact]
    public async Task Turning_keep_original_off_uses_the_typed_routing_key()
    {
        var cut = await RenderAsync(PaymentsDlq.Messages().Take(3).ToList());

        await cut.Find(".keep-original-keys input").ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.Find(".republish-routing-key input").InputAsync(new ChangeEventArgs { Value = "payment.retry" });
        await SubmitAsync(cut);

        _published.Select(r => r.RoutingKey).Should().AllBe("payment.retry");
    }

    [Fact]
    public async Task A_peeked_copy_warns_that_the_original_stays()
    {
        var cut = await RenderAsync(PaymentsDlq.Messages().Take(1).ToList(), isCopy: true);

        cut.Find(".copy-warning").TextContent.Should().Contain("The original stays in the queue — this publishes a copy.");
    }

    [Fact]
    public async Task Prod_republish_is_confirmed_first_and_cancel_publishes_nothing()
    {
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderAsync(PaymentsDlq.Messages().Take(2).ToList(), prod: true);

        await SubmitAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Republish", PaymentsDlq.SourceExchange, true, 2, Arg.Any<CancellationToken>());
        _published.Should().BeEmpty();
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default(DialogResult)!);
    }

    [Fact]
    public async Task A_failure_is_shown_inline_and_the_dialog_stays_open()
    {
        Operations.PublishAsync(default!, default!, default!, default).ThrowsAsyncForAnyArgs(new TimeoutException());
        var cut = await RenderAsync(PaymentsDlq.Messages().Take(1).ToList());

        await SubmitAsync(cut);

        cut.Find(".republish-error").TextContent.Should().Contain("Timed out talking to the broker");
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default(DialogResult)!);
    }
}
