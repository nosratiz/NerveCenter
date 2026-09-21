using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class ReceivePageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly SbConsole.Sdk.IConnectionProvider _connections = Substitute.For<SbConsole.Sdk.IConnectionProvider>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public ReceivePageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<SbConsole.Sdk.IAuditScope>());
        Services.AddSingleton<ReceiveMessagesCommandHandler>();
        Services.AddSingleton<DeleteMessageCommandHandler>();
        Services.AddSingleton<ReleaseMessageCommandHandler>();
        Services.AddLogging();
    }

    private static ReceivedMessage Message(string id, int receiveCount) =>
        new(id, $"handle-{id}", $"body-{id}", receiveCount, DateTimeOffset.UtcNow, "sender", "md5", new Dictionary<string, string> { ["source"] = "checkout" });

    // Receive.razor's ConnectionId/QueueUrl/QueueName are all [SupplyParameterFromQuery] (they
    // arrive as real query-string values via Queues.razor's link) -- bUnit refuses
    // ComponentParameterCollectionBuilder.Add() for those (it throws telling you to navigate
    // instead), so route through the fake NavigationManager the same way
    // SbConsole.Plugins.ServiceBus.Tests.Pages.PeekPageTests does for its own
    // [SupplyParameterFromQuery] parameters.
    private IRenderedComponent<SbConsole.Plugins.Aws.Pages.Receive> RenderPage()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uri = navigationManager.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["ConnectionId"] = _connectionId,
            ["QueueUrl"] = "https://sqs/orders",
            ["QueueName"] = "orders",
        });
        navigationManager.NavigateTo(uri);

        return Render<SbConsole.Plugins.Aws.Pages.Receive>();
    }

    [Fact]
    public async Task Receive_button_calls_the_handler_and_renders_returned_messages()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("m1");
        cut.Markup.Should().Contain("SQS has no peek");
    }

    [Fact]
    public async Task Held_messages_show_the_countdown_banner()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".hold-banner").Should().ContainSingle();
        cut.Find("button.release-now").Should().NotBeNull();
        // The banner's text includes an actual (numeric) countdown, not just a static message --
        // decrementing over real wall-clock time isn't practically assertable in a component test,
        // so this only checks the number is present right after a successful receive. The pure
        // remaining-seconds calculation itself is covered by ReceiveCountdownTests.
        cut.Find(".hold-banner-countdown").TextContent.Should().MatchRegex(@"~\d+ more second");
    }

    [Fact]
    public async Task High_receive_count_shows_the_bad_severity_badge()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 6) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".receive-count-bad").Should().ContainSingle();
    }

    [Fact]
    public async Task Selecting_a_message_shows_its_receipt_handle_note()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find(".message-row").Click();
        cut.Render();

        cut.Markup.Should().Contain("valid only while hidden");
    }

    [Fact]
    public async Task Delete_selected_calls_the_delete_handler_for_each_checked_message()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1), Message("m2", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();
        cut.FindAll(".select-message input")[0].Change(true);
        cut.Render();
        cut.Find("button.delete-selected").Click();
        await Task.Delay(30);

        await _operations.Received(1).DeleteMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-m1", Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), "handle-m2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Release_now_releases_every_currently_held_message()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1), Message("m2", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.release-now").Click();
        await Task.Delay(30);

        await _operations.Received(1).ChangeMessageVisibilityAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-m1", 0, Arg.Any<CancellationToken>());
        await _operations.Received(1).ChangeMessageVisibilityAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-m2", 0, Arg.Any<CancellationToken>());
    }
}
