using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class ListTopicsQueryHandlerTests
{
    [Fact]
    public async Task Returns_topics_from_operations()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var topics = new List<TopicSummary> { new("order-events-topic", "arn:aws:sns:us-east-1:1:order-events-topic", false, 3, 0, false) };
        operations.ListTopicsAsync("mode=access-keys;region=us-east-1", Arg.Any<CancellationToken>()).Returns(topics);
        var handler = new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(topics);
    }

    [Fact]
    public async Task Unknown_connection_fails()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var handler = new ListTopicsQueryHandler(Substitute.For<ISnsOperations>(), connections, NullLogger<ListTopicsQueryHandler>.Instance);

        var result = await handler.HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }
}
