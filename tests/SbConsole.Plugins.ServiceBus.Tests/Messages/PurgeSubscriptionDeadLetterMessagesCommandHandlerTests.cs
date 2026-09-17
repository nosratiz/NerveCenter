using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class PurgeSubscriptionDeadLetterMessagesCommandHandlerTests
{
    [Fact]
    public async Task Purges_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.PurgeSubscriptionDeadLetterMessagesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>()).Returns(3);
        var audit = Substitute.For<IAuditScope>();

        var result = await new PurgeSubscriptionDeadLetterMessagesCommandHandler(operations, connections, audit, NullLogger<PurgeSubscriptionDeadLetterMessagesCommandHandler>.Instance)
            .HandleAsync(new PurgeSubscriptionDeadLetterMessagesCommand(connectionId, "sb-dev", "orders", "uk-team"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3);
        await audit.Received(1).RecordAsync("subscription.purge", "sb-dev/orders/uk-team", ActionRisk.Destructive, true, "3 messages purged", Arg.Any<CancellationToken>());
    }
}
