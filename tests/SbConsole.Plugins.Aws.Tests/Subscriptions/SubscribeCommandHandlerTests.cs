using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class SubscribeCommandHandlerTests
{
    [Fact]
    public async Task Subscribes_and_audits_as_mutating()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var request = new SubscribeRequest("arn:topic", "sqs", "shipment-updates", false);
        operations.SubscribeAsync("mode=access-keys;region=us-east-1", request, Arg.Any<CancellationToken>()).Returns("arn:sub-1");
        var handler = new SubscribeCommandHandler(operations, connections, audit, NullLogger<SubscribeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new SubscribeCommand(connectionId, "aws-dev", "shipment-updates-topic", request));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("arn:sub-1");
        await audit.Received(1).RecordAsync("aws.subscription.subscribe", "aws-dev/shipment-updates-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }
}
