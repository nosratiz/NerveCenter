using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class PublishCommandHandlerTests
{
    [Fact]
    public async Task Publishes_and_audits_as_mutating()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var request = new SnsPublishRequest("Shipment dispatched", "{}", null, null, null);
        var handler = new PublishCommandHandler(operations, connections, audit, NullLogger<PublishCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PublishCommand(connectionId, "aws-dev", "shipment-updates-topic", "arn:topic", request));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).PublishAsync("mode=access-keys;region=us-east-1", "arn:topic", request, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.topic.publish", "aws-dev/shipment-updates-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }
}
