using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.ConsumerGroups;

public class GetConsumerGroupDetailQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_groups_detail_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var detail = new ConsumerGroupDetail("order-processors", "Empty", [], []);
        operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>()).Returns(detail);

        var result = await new GetConsumerGroupDetailQueryHandler(operations, connections, NullLogger<GetConsumerGroupDetailQueryHandler>.Instance)
            .HandleAsync(connectionId, "order-processors");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(detail);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new GetConsumerGroupDetailQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<GetConsumerGroupDetailQueryHandler>.Instance)
            .HandleAsync(Guid.NewGuid(), "order-processors");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ConsumerGroupDetail>(new InvalidOperationException("group not found")));

        var result = await new GetConsumerGroupDetailQueryHandler(operations, connections, NullLogger<GetConsumerGroupDetailQueryHandler>.Instance)
            .HandleAsync(connectionId, "order-processors");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("group not found");
    }
}
