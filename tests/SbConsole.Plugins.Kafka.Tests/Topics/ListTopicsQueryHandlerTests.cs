using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Topics;

public class ListTopicsQueryHandlerTests
{
    [Fact]
    public async Task Returns_topics_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var topics = new[] { new TopicSummary("orders", 3, 1, 42) };
        operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>()).Returns(topics);

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(topics);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListTopicsQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<TopicSummary>>(new InvalidOperationException("cluster unreachable")));

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("cluster unreachable");
    }
}
