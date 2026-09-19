using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.DeadLetter;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class DeadLetterOverviewPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public DeadLetterOverviewPageTests()
    {
        var connectionInfo = new ConnectionInfo(_connectionId, "kafka-dev", "kafka", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<ListDeadLetterOverviewQueryHandler>();
    }

    [Fact]
    public async Task Lists_dead_letter_topics_across_connections()
    {
        _operations.ListDeadLetterTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("orders-dlq", "orders", 3, 5, DateTimeOffset.UtcNow) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("kafka-dev");
        cut.Markup.Should().Contain("orders-dlq");
        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("5");
    }

    [Fact]
    public async Task No_dead_letter_topics_shows_an_honest_empty_state()
    {
        _operations.ListDeadLetterTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary>());

        var cut = Render<SbConsole.Plugins.Kafka.Pages.DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No dead-letter topics found");
    }

    [Fact]
    public async Task Peek_link_carries_connection_topic_and_partition_count()
    {
        _operations.ListDeadLetterTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("orders-dlq", "orders", 3, 5, DateTimeOffset.UtcNow) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".peek-dead-letter").GetAttribute("href").Should().Be(
            $"/p/kafka/topics/orders-dlq/peek?connectionId={_connectionId}&partitionCount=3");
    }
}
