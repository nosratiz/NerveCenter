using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Queues;

public class DeleteQueueCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_queue_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new DeleteQueueCommand(connectionId, "sb-dev", false, "orders-inbound"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteQueueAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("queue.delete", "sb-dev/orders-inbound", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
