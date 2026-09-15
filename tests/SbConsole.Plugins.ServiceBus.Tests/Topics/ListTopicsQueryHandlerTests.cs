using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Topics;

public class ListTopicsQueryHandlerTests
{
    [Fact]
    public async Task Returns_topics_with_aggregated_subscription_counts()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096, 3) });
        operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active"), new("eu-team", 2, 0, 2, "Active") });

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        var row = result.Value.Should().ContainSingle().Subject;
        row.Topic.Name.Should().Be("orders");
        row.ActiveMessageCount.Should().Be(7);
        row.DeadLetterMessageCount.Should().Be(1);
        row.Subscriptions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_topic_with_no_subscriptions_aggregates_to_zero()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("empty-topic", 0, 0, 0) });
        operations.ListSubscriptionsAsync("Endpoint=sb://real", "empty-topic", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary>());

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        var row = result.Value.Should().ContainSingle().Subject;
        row.ActiveMessageCount.Should().Be(0);
        row.DeadLetterMessageCount.Should().Be(0);
        row.Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListTopicsQueryHandler(Substitute.For<IServiceBusOperations>(), connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<TopicSummary>>(new InvalidOperationException("namespace unreachable")));

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("namespace unreachable");
    }
}
