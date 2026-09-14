using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests
{
    [Fact]
    public async Task Resubmits_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ResubmitSubscriptionDeadLetterMessagesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>()).Returns(1);
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResubmitSubscriptionDeadLetterMessagesCommandHandler(operations, connections, audit, NullLogger<ResubmitSubscriptionDeadLetterMessagesCommandHandler>.Instance)
            .HandleAsync(new ResubmitSubscriptionDeadLetterMessagesCommand(connectionId, "sb-dev", "orders", "uk-team", [42]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        await audit.Received(1).RecordAsync("message.resubmit", "sb-dev/orders/uk-team", ActionRisk.Mutating, true, "1 of 1 resubmitted", Arg.Any<CancellationToken>());
    }
}
