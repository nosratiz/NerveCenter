using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class ResubmitDeadLetterMessagesCommandHandlerTests
{
    [Fact]
    public async Task Resubmits_the_named_messages_and_audits_the_count()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ResubmitDeadLetterMessagesAsync("Endpoint=sb://real", "orders-inbound", Arg.Is<IReadOnlyList<long>>(l => l.SequenceEqual(new long[] { 1, 2, 3 })), Arg.Any<CancellationToken>())
            .Returns(3);
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResubmitDeadLetterMessagesCommandHandler(operations, connections, audit, NullLogger<ResubmitDeadLetterMessagesCommandHandler>.Instance)
            .HandleAsync(new ResubmitDeadLetterMessagesCommand(connectionId, "sb-dev", "orders-inbound", [1, 2, 3]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3);
        await audit.Received(1).RecordAsync("message.resubmit", "sb-dev/orders-inbound", ActionRisk.Mutating, true, "3 of 3 resubmitted", Arg.Any<CancellationToken>());
    }
}
