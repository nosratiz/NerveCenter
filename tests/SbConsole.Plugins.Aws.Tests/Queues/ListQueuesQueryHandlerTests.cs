using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class ListQueuesQueryHandlerTests
{
    [Fact]
    public async Task Returns_queues_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var queues = new[] { new QueueSummary("orders", "https://sqs/orders", "arn", false, 10, 0, 0, false, false, DateTimeOffset.UtcNow) };
        operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", "order", Arg.Any<CancellationToken>()).Returns(queues);

        var result = await new ListQueuesQueryHandler(operations, connections, NullLogger<ListQueuesQueryHandler>.Instance).HandleAsync(connectionId, "order");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(queues);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListQueuesQueryHandler(Substitute.For<ISqsOperations>(), connections, NullLogger<ListQueuesQueryHandler>.Instance).HandleAsync(Guid.NewGuid(), null);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<QueueSummary>>(new InvalidOperationException("region unreachable")));

        var result = await new ListQueuesQueryHandler(operations, connections, NullLogger<ListQueuesQueryHandler>.Instance).HandleAsync(connectionId, null);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("region unreachable");
    }
}
