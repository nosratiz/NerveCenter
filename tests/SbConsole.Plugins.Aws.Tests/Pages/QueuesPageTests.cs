using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class QueuesPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();

    public QueuesPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "aws-dev", "aws", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("aws", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { _connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        Services.AddSingleton(_dialogService);
        Services.AddLogging();
        Services.AddSingleton<ListQueuesQueryHandler>();
        Services.AddSingleton<GetConnectionEchoQueryHandler>();
        Services.AddSingleton<DeleteQueueCommandHandler>();
        Services.AddSingleton<CreateQueueCommandHandler>();
        Services.AddSingleton<PurgeQueueCommandHandler>();
        Services.AddSingleton<SbConsole.Plugins.Aws.Messages.SendMessageCommandHandler>();
        Services.AddSingleton<SbConsole.Plugins.Aws.Redrive.StartRedriveCommandHandler>();
    }

    private static QueueSummary Queue(string name, int deadLetterSourceCount = 0, bool isFifo = false) =>
        new(name, $"https://sqs/{name}", $"arn:aws:sqs:eu-west-1:123456789012:{name}", isFifo, 10, 2, 0, false, false, DateTimeOffset.UtcNow, deadLetterSourceCount);

    [Fact]
    public async Task Lists_queues_for_the_first_available_connection()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("order-events");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("aws", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Prefix_filter_is_labelled_Starts_with_and_re_queries_on_change()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", "events-", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("Starts with");
        cut.Find(".prefix-filter input").Input("events-");
        // The field's DebounceInterval="300" delays ValueChanged by real wall-clock time; the
        // 30ms delay used elsewhere in this file isn't enough to let it fire, so this assertion
        // needs a longer wait.
        await Task.Delay(400);
        cut.Render();

        await _operations.Received(1).ListQueuesAsync("mode=default-chain;region=eu-west-1", "events-", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_call_count_caption_reflects_1_plus_n_not_1_plus_3n()
    {
        // Correction from the mockup's "1 + 3n" caption -- GetQueueAttributes with
        // AttributeNames=[All] returns every attribute in one call per queue (design spec §5).
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("a"), Queue("b"), Queue("c") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".refresh-cost-caption").TextContent.Should().Contain("1 + 3 = 4");
    }

    [Fact]
    public async Task Connection_echo_renders_safe_fields_and_never_leaks_credentials()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3tKEY");
        _operations.ListQueuesAsync("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3tKEY", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        var echo = cut.Find(".connection-echo");
        echo.TextContent.Should().Contain("region=eu-west-1");
        cut.Markup.Should().NotContain("s3cr3tKEY");
    }

    [Fact]
    public async Task A_queue_with_a_dead_letter_target_shows_the_DLQ_flag()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events-dlq", deadLetterSourceCount: 1) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".dlq-flag").Should().ContainSingle();
    }

    [Fact]
    public async Task A_source_queue_with_a_configured_redrive_policy_but_no_incoming_redrives_does_NOT_show_the_DLQ_chip_or_Redrive_button()
    {
        // "order-events" has its own RedrivePolicy (it dead-letters TO order-events-dlq), which
        // made it look like a DLQ under the old (inverted) HasDeadLetterTarget-based logic. It is
        // not itself a redrive target -- DeadLetterSourceCount is 0 -- so neither the chip nor the
        // Redrive button should render for it.
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events", deadLetterSourceCount: 0) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".dlq-flag").Should().BeEmpty();
        cut.FindAll(".redrive-action").Should().BeEmpty();
    }

    [Fact]
    public async Task A_queue_that_is_a_redrive_target_for_other_queues_shows_the_DLQ_chip_and_Redrive_button()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events-dlq", deadLetterSourceCount: 2) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".dlq-flag").Should().ContainSingle();
        cut.Find(".dlq-flag").TextContent.Should().Contain("2");
        cut.FindAll(".redrive-action").Should().ContainSingle();
    }

    [Fact]
    public async Task A_FIFO_queue_shows_the_FIFO_flag()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("billing-retry.fifo", isFifo: true) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".fifo-flag").Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "order-events", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-queue").Click();
        await Task.Delay(30);

        await confirmation.Received(1).ConfirmAsync("Delete", "order-events", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/order-events", Arg.Any<CancellationToken>());
    }
}
