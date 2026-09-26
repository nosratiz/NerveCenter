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

public class CreateQueueDialogTests : RabbitPageTestBase
{
    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderCreateAsync()
    {
        var cut = RenderDialog<CreateQueueDialog>(new Dictionary<string, object?>
        {
            [nameof(CreateQueueDialog.ConnectionId)] = Dev.Id,
            [nameof(CreateQueueDialog.ConnectionName)] = Dev.Name,
            [nameof(CreateQueueDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(CreateQueueDialog.ExchangeNames)] = new List<string> { "orders.dlx", "order-events" },
        });
        await SettleAsync(cut);
        return cut;
    }

    private static Task SelectAsync<T>(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, T value)
    {
        var select = cut.FindComponents<MudSelect<T>>().Single(s => (s.Instance.Class ?? "").Split(' ').Contains(cssClass));
        return cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
    }

    [Fact]
    public async Task Name_is_required()
    {
        var cut = await RenderCreateAsync();
        cut.Find("button.create-queue-submit").HasAttribute("disabled").Should().BeTrue();

        await cut.Find(".queue-name input").InputAsync(new ChangeEventArgs { Value = "payments.q" });
        cut.Find("button.create-queue-submit").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Quorum_forces_durable_on_and_disables_the_switch()
    {
        var cut = await RenderCreateAsync();
        await cut.Find(".durable-toggle input").ChangeAsync(new ChangeEventArgs { Value = false });
        cut.Find(".durable-toggle input").HasAttribute("disabled").Should().BeFalse();

        await SelectAsync(cut, "queue-type", "quorum");
        cut.Render();

        var durable = cut.Find(".durable-toggle input");
        durable.HasAttribute("disabled").Should().BeTrue();
        durable.HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public async Task Submit_maps_every_field_into_the_request_and_closes()
    {
        var cut = await RenderCreateAsync();
        await cut.Find(".queue-name input").InputAsync(new ChangeEventArgs { Value = " payments.q " });
        await SelectAsync(cut, "queue-type", "quorum");
        await cut.Find(".auto-delete-toggle input").ChangeAsync(new ChangeEventArgs { Value = true });
        await SelectAsync(cut, "dead-letter-exchange", "orders.dlx");
        await cut.Find(".dead-letter-routing-key input").InputAsync(new ChangeEventArgs { Value = "payments.dead" });
        await cut.Find(".message-ttl input").InputAsync(new ChangeEventArgs { Value = "30000" });
        await cut.Find(".max-length input").InputAsync(new ChangeEventArgs { Value = "500000" });
        await SelectAsync(cut, "overflow", "reject-publish-dlx");
        cut.Render();

        await cut.Find("button.create-queue-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).CreateQueueAsync(DevSecret, SeededTopology.Vhost,
            new CreateQueueRequest("payments.q", "quorum", Durable: true, AutoDelete: true, DeadLetterExchange: "orders.dlx",
                DeadLetterRoutingKey: "payments.dead", MessageTtlMs: 30_000, MaxLength: 500_000, Overflow: "reject-publish-dlx"),
            Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Optional_fields_left_empty_are_sent_as_null_with_classic_durable_defaults()
    {
        var cut = await RenderCreateAsync();
        await cut.Find(".queue-name input").InputAsync(new ChangeEventArgs { Value = "plain.q" });

        await cut.Find("button.create-queue-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).CreateQueueAsync(DevSecret, SeededTopology.Vhost,
            new CreateQueueRequest("plain.q"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_create_shows_the_error_inline_and_stays_open()
    {
        Operations.CreateQueueAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(406, "PUT", "/api/queues/%2Forders/plain.q", "inequivalent arg 'durable'"));
        var cut = await RenderCreateAsync();
        await cut.Find(".queue-name input").InputAsync(new ChangeEventArgs { Value = "plain.q" });

        await cut.Find("button.create-queue-submit").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".create-queue-error").TextContent.Should().Contain("406");
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default(DialogResult)!);
    }
}
