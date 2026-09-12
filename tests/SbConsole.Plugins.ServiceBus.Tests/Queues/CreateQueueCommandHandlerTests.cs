using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Queues;

public class CreateQueueCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_queue_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new CreateQueueCommand(connectionId, "sb-dev", "orders-inbound", 10));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateQueueAsync("Endpoint=sb://real", Arg.Is<CreateQueueRequest>(r => r.Name == "orders-inbound" && r.MaxDeliveryCount == 10), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("queue.create", "sb-dev/orders-inbound", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.CreateQueueAsync(Arg.Any<string>(), Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new CreateQueueCommand(connectionId, "sb-dev", "orders-inbound", 10));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already exists");
        await audit.Received(1).RecordAsync("queue.create", "sb-dev/orders-inbound", ActionRisk.Mutating, false, "already exists", Arg.Any<CancellationToken>());
    }
}
