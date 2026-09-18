using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Pages;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class TopicsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();

    public TopicsPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "kafka-dev", "kafka", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { _connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        // Overrides MudServices' real IDialogService so the "Produce" button test can verify the
        // dialog is requested without rendering a MudDialogProvider -- same pattern
        // SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs uses.
        Services.AddSingleton(_dialogService);
        Services.AddLogging();
        Services.AddSingleton<ListTopicsQueryHandler>();
        Services.AddSingleton<CreateTopicCommandHandler>();
        Services.AddSingleton<DeleteTopicCommandHandler>();
        Services.AddSingleton<GetConnectionEchoQueryHandler>();
    }

    [Fact]
    public async Task Lists_topics_for_the_first_available_connection()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 42) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("42");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 0) });
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(30);

        await confirmation.Received(1).ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteTopicAsync("bootstrap.servers=real:9092", "orders", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_does_nothing_when_confirmation_is_denied()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 0) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(30);

        await _operations.DidNotReceive().DeleteTopicAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Filter_box_narrows_the_visible_rows()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 42), new("payments", 2, 1, 10) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".topic-filter input").Input("pay");
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("payments");
        cut.Markup.Should().NotContain("orders");
    }

    [Fact]
    public async Task Summary_line_renders_the_correct_topic_and_partition_counts()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 42), new("payments", 2, 1, 10) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        var summary = cut.Find(".topics-summary");
        summary.TextContent.Should().Contain("2 topics").And.Contain("5 partitions");
    }

    [Fact]
    public async Task Connection_echo_renders_safe_fields_and_never_leaks_the_password()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns("bootstrap.servers=real:9092;sasl.username=alice;sasl.password=s3cr3tPW");
        _operations.ListTopicsAsync("bootstrap.servers=real:9092;sasl.username=alice;sasl.password=s3cr3tPW", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary>());

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        var echo = cut.Find(".connection-echo");
        echo.TextContent.Should().Contain("bootstrap.servers=real:9092");
        cut.Markup.Should().NotContain("s3cr3tPW");
        cut.Markup.Should().NotContain("alice");
    }

    [Fact]
    public async Task Internal_topic_shows_the_internal_badge_and_read_only_label_with_no_buttons()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("__consumer_offsets", 50, 1, 0), new("orders", 3, 3, 42) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("internal");
        cut.Markup.Should().Contain("read-only");
        cut.FindAll("button.delete-topic").Should().ContainSingle();
    }

    [Fact]
    public async Task Low_replication_topic_shows_the_RF_badge_and_a_replicated_topic_does_not()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("risky", 2, 1, 0), new("safe", 2, 3, 0) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".low-replication-badge").Should().ContainSingle();
        cut.Markup.Should().Contain("RF 1");
    }

    [Fact]
    public async Task A_DLQ_suffixed_topic_shows_the_DLQ_badge()
    {
        // ReplicationFactor 3 (not <= 1) so only the DLQ-badge branch can possibly fire, not the
        // low-replication one -- keeps this test unambiguous about which condition is being checked.
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders-dlq", 3, 3, 5) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("dlq-badge");
    }

    [Fact]
    public async Task A_non_DLQ_topic_does_not_show_the_DLQ_badge()
    {
        // RF 3 (not <= 1) and not "-dlq"-suffixed, so neither the low-replication nor the DLQ-badge
        // branch fires -- keeps this test unambiguous about which condition is being checked.
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 3, 5) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().NotContain("dlq-badge");
    }

    [Fact]
    public async Task Produce_button_opens_the_produce_dialog_for_the_rows_topic()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 42) });
        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
        _dialogService.ShowAsync<ProduceMessageDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(Task.FromResult(dialogReference));

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.produce-action").Click();
        await Task.Delay(30);

        await _dialogService.Received(1).ShowAsync<ProduceMessageDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>());
    }

    [Fact]
    public async Task Producing_a_message_reloads_the_topic_list_so_the_row_reflects_the_new_message_count()
    {
        // Consistency guard: OpenCreate already reloads the topic list on success (so a newly
        // created row appears); OpenProduce used to discard the dialog result entirely and never
        // reload, leaving a stale message count after a successful produce.
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 42) });
        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
        _dialogService.ShowAsync<ProduceMessageDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(Task.FromResult(dialogReference));

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.produce-action").Click();
        await Task.Delay(30);

        await _operations.Received(2).ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelling_the_produce_dialog_does_not_reload_the_topic_list()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 42) });
        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _dialogService.ShowAsync<ProduceMessageDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(Task.FromResult(dialogReference));

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.produce-action").Click();
        await Task.Delay(30);

        await _operations.Received(1).ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>());
    }
}
