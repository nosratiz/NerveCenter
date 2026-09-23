using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class GetTopicDeliveryFailureCountQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_count_from_operations()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        operations.GetDeliveryFailureCountAsync("mode=access-keys;region=us-east-1", "shipment-updates-topic", Arg.Any<CancellationToken>()).Returns(1204L);
        var handler = new GetTopicDeliveryFailureCountQueryHandler(operations, connections, NullLogger<GetTopicDeliveryFailureCountQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId, "shipment-updates-topic");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1204L);
    }

    [Fact]
    public async Task A_denied_metrics_call_fails_this_handler_without_throwing()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        operations.GetDeliveryFailureCountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(new InvalidOperationException("AccessDenied")));
        var handler = new GetTopicDeliveryFailureCountQueryHandler(operations, connections, NullLogger<GetTopicDeliveryFailureCountQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId, "shipment-updates-topic");

        result.IsSuccess.Should().BeFalse();
    }
}
