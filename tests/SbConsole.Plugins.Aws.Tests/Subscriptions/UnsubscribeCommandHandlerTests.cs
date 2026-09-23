using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class UnsubscribeCommandHandlerTests
{
    [Fact]
    public async Task Unsubscribes_and_audits_as_mutating_not_destructive()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var handler = new UnsubscribeCommandHandler(operations, connections, audit, NullLogger<UnsubscribeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new UnsubscribeCommand(connectionId, "aws-dev", "shipment-updates-topic", "arn:sub-1"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).UnsubscribeAsync("mode=access-keys;region=us-east-1", "arn:sub-1", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.subscription.unsubscribe", "aws-dev/shipment-updates-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }
}
