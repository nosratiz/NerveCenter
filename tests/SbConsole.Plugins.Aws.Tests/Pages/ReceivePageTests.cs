using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Plugins.Aws.Pages;
using SbConsole.Plugins.Aws.Queues;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class ReceivePageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly SbConsole.Sdk.IConnectionProvider _connections = Substitute.For<SbConsole.Sdk.IConnectionProvider>();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();
    private readonly Guid _connectionId = Guid.NewGuid();

    private const string Secret = "mode=default-chain;region=eu-west-1";
    private const string SourceA = "https://sqs/orders-a";
    private const string SourceB = "https://sqs/orders-b";

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
        Services.AddSingleton<MoveMessageToSourceCommandHandler>();
        Services.AddSingleton<GetQueueDetailQueryHandler>();
        Services.AddSingleton(_dialogService);
        Services.AddLogging();
    }

    // The Receive page's own queue is https://sqs/orders; `sources` is what SQS's
    // ListDeadLetterSourceQueues reported for it (null = that lookup failed).
    private void GivenSources(IReadOnlyList<string>? sources) =>
        _operations.GetQueueDetailAsync(Secret, "https://sqs/orders", Arg.Any<CancellationToken>())
            .Returns(new QueueDetails("orders", "https://sqs/orders", "arn:aws:sqs:eu-west-1:123456789012:orders", false, false,
                1, 0, 0, null, null, new Dictionary<string, string>(), null, null, sources));

    private async Task<IRenderedComponent<Receive>> RenderAndReceiveAsync(params ReceivedMessage[] messages)
    {
        _operations.ReceiveMessagesAsync(Secret, "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(messages.ToList());
        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();
        return cut;
    }

    private static ReceivedMessage Message(string id, int receiveCount) =>
        new(id, $"handle-{id}", $"body-{id}", receiveCount, DateTimeOffset.UtcNow, "sender", "md5", new Dictionary<string, SqsMessageAttribute> { ["source"] = new("String", "checkout", null) });

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

    [Fact]
    public async Task A_non_DLQ_has_no_move_to_source_action()
    {
        GivenSources([]);

        var cut = await RenderAndReceiveAsync(Message("m1", 1));

        cut.FindAll("button.move-to-source").Should().BeEmpty();
        cut.FindAll(".move-to-source-unavailable").Should().BeEmpty();
    }

    [Fact]
    public async Task When_the_source_lookup_failed_the_action_is_hidden_with_an_explanation()
    {
        GivenSources(null);

        var cut = await RenderAndReceiveAsync(Message("m1", 1));

        cut.FindAll("button.move-to-source").Should().BeEmpty();
        cut.Find(".move-to-source-unavailable").TextContent.Should().Contain("ListDeadLetterSourceQueues");
    }

    [Fact]
    public async Task A_DLQ_with_one_source_moves_the_message_directly_and_removes_the_row()
    {
        GivenSources([SourceA]);

        var cut = await RenderAndReceiveAsync(Message("m1", 6), Message("m2", 6));
        cut.FindAll("button.move-to-source").Should().HaveCount(2);
        cut.FindAll("button.move-to-source")[0].Click();
        await Task.Delay(30);
        cut.Render();

        await _operations.Received(1).SendMessageAsync(Secret, SourceA, Arg.Is<SendMessageRequest>(r => r.Body == "body-m1"), Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteMessageAsync(Secret, "https://sqs/orders", "handle-m1", Arg.Any<CancellationToken>());
        await _dialogService.DidNotReceiveWithAnyArgs().ShowAsync<MoveToSourceDialog>(default, default(DialogParameters)!);
        cut.FindAll(".message-row").Should().ContainSingle();
        cut.Markup.Should().NotContain("handle-m1");
    }

    [Fact]
    public async Task A_DLQ_with_several_sources_asks_which_one_then_moves_to_the_picked_source()
    {
        GivenSources([SourceA, SourceB]);
        var reference = Substitute.For<IDialogReference>();
        reference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(SourceB)));
        _dialogService.ShowAsync<MoveToSourceDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>()).Returns(reference);

        var cut = await RenderAndReceiveAsync(Message("m1", 6));
        cut.Find("button.move-to-source").Click();
        await Task.Delay(30);
        cut.Render();

        await _operations.Received(1).SendMessageAsync(Secret, SourceB, Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().SendMessageAsync(Secret, SourceA, Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
        cut.FindAll(".message-row").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_the_source_picker_moves_nothing()
    {
        GivenSources([SourceA, SourceB]);
        var reference = Substitute.For<IDialogReference>();
        reference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _dialogService.ShowAsync<MoveToSourceDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>()).Returns(reference);

        var cut = await RenderAndReceiveAsync(Message("m1", 6));
        cut.Find("button.move-to-source").Click();
        await Task.Delay(30);
        cut.Render();

        await _operations.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default!, default);
        cut.FindAll(".message-row").Should().ContainSingle();
    }

    [Fact]
    public async Task The_detail_pane_shows_typed_and_binary_message_attributes()
    {
        var message = Message("m1", 1) with
        {
            MessageAttributes = new Dictionary<string, SqsMessageAttribute>
            {
                ["count"] = new("Number", "3", null),
                ["blob"] = new("Binary", null, [1, 2, 3]),
            },
        };

        var cut = await RenderAndReceiveAsync(message);

        var attributes = cut.FindAll(".message-attribute").Select(e => e.TextContent.Trim()).ToList();
        attributes.Should().Contain(a => a.Contains("count") && a.Contains("Number") && a.Contains('3'));
        attributes.Should().Contain(a => a.Contains("blob") && a.Contains("(binary, 3 bytes)"));
    }
}
