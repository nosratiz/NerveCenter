using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class ListSubscriptionsQueryHandlerTests
{
    [Fact]
    public async Task Returns_subscriptions_from_operations()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var subs = new List<SubscriptionSummary> { new("arn:sub-1", "sqs", "shipment-updates", false, false, null) };
        operations.ListSubscriptionsAsync("mode=access-keys;region=us-east-1", "arn:topic", Arg.Any<CancellationToken>()).Returns(subs);
        var handler = new ListSubscriptionsQueryHandler(operations, connections, NullLogger<ListSubscriptionsQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId, "arn:topic");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(subs);
    }
}
