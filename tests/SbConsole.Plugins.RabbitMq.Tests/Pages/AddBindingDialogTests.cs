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

public class AddBindingDialogTests : RabbitPageTestBase
{
    private const string Queue = "order-events.q";

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderAsync()
    {
        var cut = RenderDialog<AddBindingDialog>(new Dictionary<string, object?>
        {
            [nameof(AddBindingDialog.ConnectionId)] = Dev.Id,
            [nameof(AddBindingDialog.ConnectionName)] = Dev.Name,
            [nameof(AddBindingDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(AddBindingDialog.Queue)] = Queue,
            [nameof(AddBindingDialog.Exchanges)] = SeededTopology.Exchanges().Where(e => !e.IsDefault).ToList(),
        });
        await SettleAsync(cut);
        return cut;
    }

    private static Task SelectAsync<T>(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, T value)
    {
        var select = cut.FindComponents<MudSelect<T>>().Single(s => (s.Instance.Class ?? "").Split(' ').Contains(cssClass));
        return cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
    }

    private static bool SubmitDisabled(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.Find("button.add-binding-submit").HasAttribute("disabled");

    [Fact]
    public async Task An_exchange_is_required_and_its_type_is_shown()
    {
        var cut = await RenderAsync();
        SubmitDisabled(cut).Should().BeTrue();

        await SelectAsync(cut, "binding-exchange", "order-events");

        SubmitDisabled(cut).Should().BeFalse();
        cut.Find(".binding-exchange-type").TextContent.Trim().Should().Be("topic");
    }

    [Fact]
    public async Task Submitting_binds_with_the_routing_key_and_closes()
    {
        var cut = await RenderAsync();
        await SelectAsync(cut, "binding-exchange", "order-events");
        await cut.Find(".binding-routing-key input").InputAsync(new ChangeEventArgs { Value = "order.*.cancelled" });

        await cut.Find("button.add-binding-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).AddBindingAsync(DevSecret, SeededTopology.Vhost, "order-events", Queue, "order.*.cancelled",
            Arg.Is<IReadOnlyDictionary<string, object?>>(a => a.Count == 0), Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("rabbitmq.binding.add", Arg.Any<string>(), SbConsole.Sdk.ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task A_fanout_exchange_hides_the_routing_key()
    {
        var cut = await RenderAsync();

        await SelectAsync(cut, "binding-exchange", "notify.fanout");

        cut.FindAll(".binding-routing-key").Should().BeEmpty();
        cut.Find(".fanout-caption").TextContent.Trim().Should().Be("Fanout ignores the routing key");
        cut.FindAll(".binding-arguments").Should().BeEmpty();

        await cut.Find("button.add-binding-submit").ClickAsync(new());
        await SettleAsync(cut);
        await Operations.Received(1).AddBindingAsync(DevSecret, SeededTopology.Vhost, "notify.fanout", Queue, "",
            Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_headers_exchange_parses_the_argument_lines_and_x_match()
    {
        var cut = await RenderAsync();
        await SelectAsync(cut, "binding-exchange", "invoice.headers");
        await cut.Find(".binding-arguments textarea").InputAsync(new ChangeEventArgs { Value = "region: eu\ntier: gold" });
        await SelectAsync(cut, "binding-x-match", "any");

        await cut.Find("button.add-binding-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).AddBindingAsync(DevSecret, SeededTopology.Vhost, "invoice.headers", Queue, Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, object?>>(a =>
                a.Count == 3 && (string)a["x-match"]! == "any" && (string)a["region"]! == "eu" && (string)a["tier"]! == "gold"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bad_argument_lines_block_submit()
    {
        var cut = await RenderAsync();
        await SelectAsync(cut, "binding-exchange", "invoice.headers");

        await cut.Find(".binding-arguments textarea").InputAsync(new ChangeEventArgs { Value = "region eu" });

        SubmitDisabled(cut).Should().BeTrue();
        cut.Markup.Should().Contain("Line 1: expected");
    }

    [Fact]
    public async Task A_failed_bind_shows_the_error_inline_and_stays_open()
    {
        Operations.AddBindingAsync(DevSecret, SeededTopology.Vhost, "order-events", Queue, Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "POST", "/api/bindings/%2Forders/e/order-events/q/order-events.q", "access refused"));
        var cut = await RenderAsync();
        await SelectAsync(cut, "binding-exchange", "order-events");

        await cut.Find("button.add-binding-submit").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".add-binding-error").TextContent.Should().Contain("403");
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default(DialogResult)!);
    }
}
