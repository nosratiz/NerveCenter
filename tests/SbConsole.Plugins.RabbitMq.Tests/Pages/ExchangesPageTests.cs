using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Tests.Components;
using ExchangesPage = SbConsole.Plugins.RabbitMq.Pages.Exchanges;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class ExchangesPageTests : RabbitPageTestBase
{
    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync()
    {
        RenderFragment page = builder =>
        {
            builder.OpenComponent<ExchangesPage>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    private static IReadOnlyList<string> Names(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut) =>
        cut.FindAll(".exchange-row .exchange-name").Select(e => e.TextContent.Trim()).ToList();

    private static IElement Row(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".exchange-row").Single(r => r.QuerySelector(".exchange-name")!.TextContent.Trim() == name);

    [Fact]
    public async Task Lists_exchanges_with_type_attributes_binding_counts_and_rates_hiding_amq_builtins()
    {
        SeededTopology.Seed(Operations, DevSecret);

        var cut = await RenderPageAsync();

        Names(cut).Should().Equal("(AMQP default)", "billing.retry.dlx", "invoice.headers", "legacy.import", "notify.fanout",
            "old.unused", "order-events", "shipment.updates", "unrouted.ae");
        cut.Find(".exchange-count").TextContent.Should().Contain("9 exchanges");

        var orders = Row(cut, "order-events");
        orders.QuerySelector(".type-chip")!.ClassList.Should().Contain("mud-chip-filled").And.Contain("mud-chip-color-primary");
        orders.QuerySelector(".type-chip")!.TextContent.Should().Contain("topic");
        orders.QuerySelector(".attr-durable").Should().NotBeNull();
        orders.QuerySelector(".binding-count")!.TextContent.Trim().Should().Be("3");
        orders.QuerySelector(".rate-in")!.TextContent.Trim().Should().Be("1,204/s");
        orders.QuerySelector(".rate-out")!.TextContent.Trim().Should().Be("1,204/s");
        orders.QuerySelector(".no-bindings").Should().BeNull();

        Row(cut, "notify.fanout").QuerySelector(".type-chip")!.ClassList.Should().Contain("mud-chip-outlined");
        Row(cut, "notify.fanout").QuerySelector(".rate-out")!.TextContent.Trim().Should().Be("1,608/s");

        var dlx = Row(cut, "billing.retry.dlx");
        dlx.QuerySelector(".attr-dlx")!.TextContent.Should().Contain("DLX");
        Row(cut, "order-events").QuerySelector(".attr-dlx").Should().BeNull();

        var defaultRow = Row(cut, "(AMQP default)");
        defaultRow.QuerySelector(".attr-built-in").Should().NotBeNull();
        defaultRow.QuerySelector(".binding-count")!.TextContent.Trim().Should().Be("8", "the default exchange counts every queue");
        defaultRow.QuerySelector(".no-bindings").Should().BeNull();

        var shipment = Row(cut, "shipment.updates");
        shipment.QuerySelector(".attr-ae")!.TextContent.Should().Contain("AE → unrouted.ae");
        shipment.QuerySelector(".no-bindings").Should().BeNull("an alternate exchange catches what the bindings miss");

        Row(cut, "unrouted.ae").QuerySelector(".attr-internal").Should().NotBeNull();
    }

    [Fact]
    public async Task Show_builtin_reveals_the_amq_exchanges_as_builtin()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var cut = await RenderPageAsync();

        await cut.Find(".show-builtin input").ChangeAsync(new ChangeEventArgs { Value = true });

        Names(cut).Should().Contain(["amq.direct", "amq.topic"]);
        cut.Find(".exchange-count").TextContent.Should().Contain("11 exchanges");
        Row(cut, "amq.topic").QuerySelector(".attr-built-in").Should().NotBeNull();
        Row(cut, "amq.direct").QuerySelector(".no-bindings").Should().BeNull("built-ins aren't the user's to bind");
    }

    [Fact]
    public async Task Type_chip_filters_by_exchange_type()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var cut = await RenderPageAsync();

        await cut.Find(".type-filter-topic").ClickAsync(new());

        Names(cut).Should().Equal("order-events", "shipment.updates");
        cut.Find(".exchange-count").TextContent.Should().Contain("2 exchanges");

        await cut.Find(".type-filter-all").ClickAsync(new());
        Names(cut).Should().HaveCount(9);
    }

    [Fact]
    public async Task Filter_box_matches_a_substring_case_insensitively()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var cut = await RenderPageAsync();

        await cut.Find(".exchange-filter input").InputAsync(new ChangeEventArgs { Value = "BILL" });

        Names(cut).Should().Equal("billing.retry.dlx");
    }

    [Fact]
    public async Task An_unbound_exchange_without_alternate_is_flagged_and_tinted_only_when_it_receives_traffic()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var cut = await RenderPageAsync();

        var legacy = Row(cut, "legacy.import");
        legacy.QuerySelector(".no-bindings")!.TextContent.Should().Contain("no bindings");
        legacy.ClassList.Should().Contain("silent-loss");
        legacy.GetAttribute("style").Should().Contain("var(--mud-palette-warning");
        legacy.QuerySelector("[title]")!.GetAttribute("title").Should().Be("Publishing with zero bindings is silent message loss");
        legacy.QuerySelector(".binding-count")!.TextContent.Trim().Should().Be("0");

        var unused = Row(cut, "old.unused");
        unused.QuerySelector(".no-bindings").Should().NotBeNull();
        unused.ClassList.Should().NotContain("silent-loss");
    }

    [Fact]
    public async Task Only_builtins_shows_the_empty_state_with_a_create_button()
    {
        Operations.ListExchangesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>())
            .Returns(new List<ExchangeSummary> { SeededTopology.Exchange("", "direct"), SeededTopology.Exchange("amq.direct", "direct") });
        Operations.ListBindingsAsync(DevSecret, "/orders", Arg.Any<CancellationToken>()).Returns(new List<BindingInfo>());
        Operations.ListQueuesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>()).Returns(new List<QueueSummary>());

        var cut = await RenderPageAsync();

        cut.Find(".exchanges-empty").TextContent.Should()
            .Contain("No exchanges declared in /orders.")
            .And.Contain("Only the built-in AMQP default is present. Publishers routing by queue name work without one.");
        cut.Find(".exchanges-empty .create-exchange").Should().NotBeNull();
        cut.FindAll(".exchange-row").Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_confirms_then_calls_the_handler_and_reloads()
    {
        SeededTopology.Seed(Operations, DevSecret);
        Confirmation.ConfirmAsync("Delete", "legacy.import", false, null, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync();

        await Row(cut, "legacy.import").QuerySelector(".delete-exchange")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).DeleteExchangeAsync(DevSecret, "/orders", "legacy.import", Arg.Any<CancellationToken>());
        await Operations.Received(2).ListExchangesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_delete_calls_nothing()
    {
        SeededTopology.Seed(Operations, DevSecret);
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderPageAsync();

        await Row(cut, "legacy.import").QuerySelector(".delete-exchange")!.ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Delete", "legacy.import", false, null, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().DeleteExchangeAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_failed_delete_shows_a_snackbar()
    {
        SeededTopology.Seed(Operations, DevSecret);
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(true);
        Operations.DeleteExchangeAsync(DevSecret, "/orders", "legacy.import", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "DELETE", "/api/exchanges/%2Forders/legacy.import", "access refused"));
        var cut = await RenderPageAsync();

        await Row(cut, "legacy.import").QuerySelector(".delete-exchange")!.ClickAsync(new());
        await SettleAsync(cut);

        var snackbar = Services.GetService(typeof(MudBlazor.ISnackbar)) as MudBlazor.ISnackbar;
        snackbar!.ShownSnackbars.Should().ContainSingle(s => s.Severity == MudBlazor.Severity.Error);
    }

    [Fact]
    public async Task Delete_is_disabled_for_builtin_exchanges()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var cut = await RenderPageAsync();
        await cut.Find(".show-builtin input").ChangeAsync(new ChangeEventArgs { Value = true });

        Row(cut, "(AMQP default)").QuerySelector(".delete-exchange")!.HasAttribute("disabled").Should().BeTrue();
        Row(cut, "amq.topic").QuerySelector(".delete-exchange")!.HasAttribute("disabled").Should().BeTrue();
        Row(cut, "order-events").QuerySelector(".delete-exchange")!.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Loading_shows_the_endpoint_being_read()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var slow = new TaskCompletionSource<IReadOnlyList<ExchangeSummary>>();
        Operations.ListExchangesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>()).Returns(slow.Task);

        var cut = await RenderPageAsync();

        cut.Find(".exchanges-loading").TextContent.Should().Contain("Reading /api/exchanges/%2Forders");
        slow.SetResult(SeededTopology.Exchanges());
        await SettleAsync(cut);
        cut.FindAll(".exchanges-loading").Should().BeEmpty();
    }

    [Fact]
    public async Task First_load_failure_shows_the_management_error_and_retry_recovers()
    {
        SeededTopology.Seed(Operations, DevSecret);
        Operations.ListExchangesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "GET", "/api/exchanges/%2Forders", "Access refused."));

        var cut = await RenderPageAsync();

        cut.Find(".management-error").TextContent.Should().Contain("403");
        cut.FindAll(".exchange-row").Should().BeEmpty();

        Operations.ListExchangesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>()).Returns(SeededTopology.Exchanges());
        await cut.Find(".retry-load").ClickAsync(new());
        await SettleAsync(cut);

        cut.FindAll(".management-error").Should().BeEmpty();
        Names(cut).Should().HaveCount(9);
    }

    [Fact]
    public async Task Refresh_failure_keeps_the_rows_dimmed_under_a_stale_banner()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var cut = await RenderPageAsync();

        Clock.Now = Clock.Now.AddMinutes(2);
        Operations.ListExchangesAsync(DevSecret, "/orders", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(503, "GET", "/api/exchanges/%2Forders", "Service Unavailable"));
        await cut.Find(".refresh-exchanges").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".stale-banner").TextContent.Should().Contain("Refresh failed —").And.Contain("Showing values from 14:03, 2m ago.");
        cut.Find(".stale-data").GetAttribute("style").Should().Contain("opacity:.55");
        cut.FindAll(".stale-data .exchange-row").Should().HaveCount(9);
        cut.FindAll(".stale-banner .toggle-auto-refresh").Should().BeEmpty("this page has no auto-refresh to pause");
        cut.FindAll(".management-error").Should().BeEmpty();
    }

    [Fact]
    public async Task Publish_opens_the_publish_dialog_preselected_with_the_rows_exchange()
    {
        SeededTopology.Seed(Operations, DevSecret);
        var dialogs = Substitute.For<MudBlazor.IDialogService>();
        Services.AddSingleton(dialogs);
        var cut = await RenderPageAsync();

        await Row(cut, "order-events").QuerySelector(".publish-exchange")!.ClickAsync(new());

        await dialogs.Received(1).ShowAsync<SbConsole.Plugins.RabbitMq.Pages.PublishDialog>(
            Arg.Any<string>(),
            Arg.Is<MudBlazor.DialogParameters>(p =>
                (string)p[nameof(SbConsole.Plugins.RabbitMq.Pages.PublishDialog.Exchange)]! == "order-events"
                && (string)p[nameof(SbConsole.Plugins.RabbitMq.Pages.PublishDialog.Vhost)]! == "/orders"
                && (Guid)p[nameof(SbConsole.Plugins.RabbitMq.Pages.PublishDialog.ConnectionId)]! == Dev.Id
                && p[nameof(SbConsole.Plugins.RabbitMq.Pages.PublishDialog.Exchanges)] != null),
            Arg.Any<MudBlazor.DialogOptions>());
    }
}
