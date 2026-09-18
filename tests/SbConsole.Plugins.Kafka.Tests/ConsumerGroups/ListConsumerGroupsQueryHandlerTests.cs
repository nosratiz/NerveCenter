using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.ConsumerGroups;

public class ListConsumerGroupsQueryHandlerTests
{
    [Fact]
    public async Task Returns_groups_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var groups = new[] { new ConsumerGroupSummary("order-processors", "Stable", 3, 120) };
        operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>()).Returns(groups);

        var result = await new ListConsumerGroupsQueryHandler(operations, connections, NullLogger<ListConsumerGroupsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(groups);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListConsumerGroupsQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<ListConsumerGroupsQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ConsumerGroupSummary>>(new InvalidOperationException("cluster unreachable")));

        var result = await new ListConsumerGroupsQueryHandler(operations, connections, NullLogger<ListConsumerGroupsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("cluster unreachable");
    }
}
