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

public class CreateShovelDialogTests : RabbitPageTestBase
{
    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderCreateAsync(bool prod = false)
    {
        var connection = prod ? Prod : Dev;
        var cut = RenderDialog<CreateShovelDialog>(new Dictionary<string, object?>
        {
            [nameof(CreateShovelDialog.ConnectionId)] = connection.Id,
            [nameof(CreateShovelDialog.ConnectionName)] = connection.Name,
            [nameof(CreateShovelDialog.IsProd)] = prod,
            [nameof(CreateShovelDialog.Vhost)] = SeededTopology.Vhost,
            [nameof(CreateShovelDialog.QueueNames)] = new List<string> { "billing.retry", "legacy.import.q", "audit.sink" },
            [nameof(CreateShovelDialog.ExchangeNames)] = new List<string> { "", "billing.direct", "order-events" },
        });
        await SettleAsync(cut);
        return cut;
    }

    private static Task SetAsync<TComponent, T>(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, T value,
        Func<TComponent, EventCallback<T>> changed)
        where TComponent : MudComponentBase
    {
        var component = cut.FindComponents<TComponent>().Single(c => (c.Instance.Class ?? "").Split(' ').Contains(cssClass));
        return cut.InvokeAsync(() => changed(component.Instance).InvokeAsync(value));
    }

    private static Task SetAutocompleteAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, string value) =>
        SetAsync<MudAutocomplete<string>, string?>(cut, cssClass, value, c => c.ValueChanged);

    private static Task SetSelectAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string cssClass, string value) =>
        SetAsync<MudSelect<string>, string?>(cut, cssClass, value, c => c.ValueChanged);

    private static Task SetKindAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string kind) =>
        SetAsync<MudToggleGroup<string>, string?>(cut, "destination-kind", kind, c => c.ValueChanged);

    private static bool SubmitDisabled(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.Find("button.create-shovel-submit").HasAttribute("disabled");

    [Fact]
    public async Task Exchange_destination_maps_exchange_and_routing_key_and_closes()
    {
        var cut = await RenderCreateAsync();
        await cut.Find(".new-shovel-name input").InputAsync(new ChangeEventArgs { Value = " billing-retry-loop " });
        await SetAutocompleteAsync(cut, "source-queue", "billing.retry");
        await SetKindAsync(cut, "exchange");
        cut.Render();
        await SetAutocompleteAsync(cut, "destination-exchange", "billing.direct");
        await cut.Find(".destination-routing-key input").InputAsync(new ChangeEventArgs { Value = "billing" });
        cut.Render();

        await cut.Find("button.create-shovel-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).CreateShovelAsync(DevSecret, SeededTopology.Vhost,
            new CreateShovelRequest("billing-retry-loop", "billing.retry", null, "billing.direct", "billing"),
            Arg.Any<CancellationToken>());
        DialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Queue_destination_maps_ack_mode_delete_after_and_advanced_uris()
    {
        var cut = await RenderCreateAsync();
        await cut.Find(".new-shovel-name input").InputAsync(new ChangeEventArgs { Value = "audit-offsite" });
        await SetAutocompleteAsync(cut, "source-queue", "audit.sink");
        await SetAutocompleteAsync(cut, "destination-queue", "audit.sink");
        await SetAsync<MudRadioGroup<string>, string>(cut, "ack-mode", "no-ack", c => c.ValueChanged);
        await SetSelectAsync(cut, "delete-after", "queue-length");
        await cut.Find(".advanced-toggle").ClickAsync(new());
        await cut.Find(".destination-uri input").InputAsync(new ChangeEventArgs { Value = "amqp://u:p@rabbit-eu-dr" });
        cut.Render();

        await cut.Find("button.create-shovel-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).CreateShovelAsync(DevSecret, SeededTopology.Vhost,
            new CreateShovelRequest("audit-offsite", "audit.sink", "audit.sink", null, null, "no-ack", null, "amqp://u:p@rabbit-eu-dr", "queue-length"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Each_ack_mode_carries_a_one_line_explanation_and_advanced_explains_amqp()
    {
        var cut = await RenderCreateAsync();

        cut.FindAll(".ack-mode-explanation").Should().HaveCount(3);
        await cut.Find(".advanced-toggle").ClickAsync(new());
        cut.Markup.Should().Contain("amqp:// with no host means this broker");
    }

    [Fact]
    public async Task Name_source_and_destination_are_required()
    {
        var cut = await RenderCreateAsync();
        SubmitDisabled(cut).Should().BeTrue();

        await cut.Find(".new-shovel-name input").InputAsync(new ChangeEventArgs { Value = "s" });
        await SetAutocompleteAsync(cut, "source-queue", "billing.retry");
        cut.Render();
        SubmitDisabled(cut).Should().BeTrue("a destination is required");

        await SetAutocompleteAsync(cut, "destination-queue", "legacy.import.q");
        cut.Render();
        SubmitDisabled(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Source_and_destination_queue_must_differ()
    {
        var cut = await RenderCreateAsync();
        await cut.Find(".new-shovel-name input").InputAsync(new ChangeEventArgs { Value = "loop" });
        await SetAutocompleteAsync(cut, "source-queue", "billing.retry");
        await SetAutocompleteAsync(cut, "destination-queue", "billing.retry");
        cut.Render();

        SubmitDisabled(cut).Should().BeTrue();
        cut.Find(".shovel-validation").TextContent.Should().Contain("Source and destination queue are the same");
    }

    [Fact]
    public async Task On_prod_the_create_is_confirmed_and_cancel_creates_nothing()
    {
        Confirmation.ConfirmAsync("Create shovel", "p1", true, null, Arg.Any<CancellationToken>()).Returns(false);
        var cut = await RenderCreateAsync(prod: true);
        await cut.Find(".new-shovel-name input").InputAsync(new ChangeEventArgs { Value = "p1" });
        await SetAutocompleteAsync(cut, "source-queue", "billing.retry");
        await SetAutocompleteAsync(cut, "destination-queue", "audit.sink");
        cut.Render();

        await cut.Find("button.create-shovel-submit").ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Create shovel", "p1", true, null, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().CreateShovelAsync(default!, default!, default!, default);

        Confirmation.ConfirmAsync("Create shovel", "p1", true, null, Arg.Any<CancellationToken>()).Returns(true);
        await cut.Find("button.create-shovel-submit").ClickAsync(new());
        await SettleAsync(cut);
        await Operations.Received(1).CreateShovelAsync(ProdSecret, SeededTopology.Vhost, Arg.Any<CreateShovelRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_create_shows_the_error_inline_and_stays_open()
    {
        Operations.CreateShovelAsync(DevSecret, SeededTopology.Vhost, Arg.Any<CreateShovelRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(400, "PUT", "/api/parameters/shovel/%2Forders/s", "bad uri"));
        var cut = await RenderCreateAsync();
        await cut.Find(".new-shovel-name input").InputAsync(new ChangeEventArgs { Value = "s" });
        await SetAutocompleteAsync(cut, "source-queue", "billing.retry");
        await SetAutocompleteAsync(cut, "destination-queue", "audit.sink");
        cut.Render();

        await cut.Find("button.create-shovel-submit").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".create-shovel-error").TextContent.Should().Contain("400");
        DialogInstance.DidNotReceiveWithAnyArgs().Close(default(DialogResult)!);
    }
}
