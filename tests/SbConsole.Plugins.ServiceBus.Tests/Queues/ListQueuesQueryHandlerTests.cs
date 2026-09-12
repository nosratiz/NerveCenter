using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Queues;

public class ListQueuesQueryHandlerTests
{
    [Fact]
    public async Task Returns_queues_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var queues = new[] { new QueueSummary("orders-inbound", 12, 0, 0, 1024) };
        operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>()).Returns(queues);

        var result = await new ListQueuesQueryHandler(operations, connections).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(queues);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListQueuesQueryHandler(Substitute.For<IServiceBusOperations>(), connections).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListQueuesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<QueueSummary>>(new InvalidOperationException("namespace unreachable")));

        var result = await new ListQueuesQueryHandler(operations, connections).HandleAsync(connectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("namespace unreachable");
    }
}
