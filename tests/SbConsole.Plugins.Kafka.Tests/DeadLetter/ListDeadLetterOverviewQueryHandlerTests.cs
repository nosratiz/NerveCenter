using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.DeadLetter;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.DeadLetter;

public class ListDeadLetterOverviewQueryHandlerTests
{
    [Fact]
    public async Task Returns_an_empty_list_when_there_are_no_Kafka_connections()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var result = await new ListDeadLetterOverviewQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Merges_dead_letter_topics_across_every_connection()
    {
        var connectionA = new ConnectionInfo(Guid.NewGuid(), "kafka-dev", "kafka", ["dev"]);
        var connectionB = new ConnectionInfo(Guid.NewGuid(), "kafka-staging", "kafka", ["staging"]);
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connectionA, connectionB });
        connections.GetSecretAsync(connectionA.Id, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=dev:9092");
        connections.GetSecretAsync(connectionB.Id, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=staging:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ListDeadLetterTopicsAsync("bootstrap.servers=dev:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("orders-dlq", "orders", 3, 5, DateTimeOffset.UtcNow) });
        operations.ListDeadLetterTopicsAsync("bootstrap.servers=staging:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("payments-dlq", "payments", 1, 0, null) });

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().BeEquivalentTo(new[]
        {
            new DeadLetterOverviewEntry(connectionA.Id, "kafka-dev", "orders-dlq", "orders", 3, 5),
            new DeadLetterOverviewEntry(connectionB.Id, "kafka-staging", "payments-dlq", "payments", 1, 0),
        });
    }

    [Fact]
    public async Task A_connection_whose_call_fails_is_skipped_not_fatal()
    {
        var failingConnection = new ConnectionInfo(Guid.NewGuid(), "kafka-unreachable", "kafka", []);
        var healthyConnection = new ConnectionInfo(Guid.NewGuid(), "kafka-dev", "kafka", ["dev"]);
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { failingConnection, healthyConnection });
        connections.GetSecretAsync(failingConnection.Id, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=unreachable:9092");
        connections.GetSecretAsync(healthyConnection.Id, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=dev:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ListDeadLetterTopicsAsync("bootstrap.servers=unreachable:9092", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<DeadLetterTopicSummary>>(new InvalidOperationException("broker unreachable")));
        operations.ListDeadLetterTopicsAsync("bootstrap.servers=dev:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("orders-dlq", "orders", 1, 2, null) });

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().ContainSingle().Which.ConnectionName.Should().Be("kafka-dev");
    }

    [Fact]
    public async Task A_connection_with_no_secret_is_skipped()
    {
        var connection = new ConnectionInfo(Guid.NewGuid(), "kafka-dev", "kafka", []);
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connection });
        connections.GetSecretAsync(connection.Id, Arg.Any<CancellationToken>()).Returns((string?)null);
        var operations = Substitute.For<IKafkaOperations>();

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().BeEmpty();
        await operations.DidNotReceive().ListDeadLetterTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
