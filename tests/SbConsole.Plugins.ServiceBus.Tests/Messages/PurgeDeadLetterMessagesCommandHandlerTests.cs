using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class PurgeDeadLetterMessagesCommandHandlerTests
{
    [Fact]
    public async Task Purges_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.PurgeDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>()).Returns(214);
        var audit = Substitute.For<IAuditScope>();

        var result = await new PurgeDeadLetterMessagesCommandHandler(operations, connections, audit)
            .HandleAsync(new PurgeDeadLetterMessagesCommand(connectionId, "sb-dev", "orders-inbound"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(214);
        await audit.Received(1).RecordAsync("queue.purge", "sb-dev/orders-inbound", ActionRisk.Destructive, true, "214 messages purged", Arg.Any<CancellationToken>());
    }
}
