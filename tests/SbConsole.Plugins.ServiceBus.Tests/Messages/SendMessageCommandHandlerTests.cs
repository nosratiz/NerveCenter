using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class SendMessageCommandHandlerTests
{
    [Fact]
    public async Task Sends_the_message_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new SendMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new SendMessageCommand(connectionId, "sb-dev", "orders-inbound", """{"a":1}""", "application/json", null, null));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).SendMessageAsync("Endpoint=sb://real", "orders-inbound", Arg.Is<SendMessageRequest>(r => r.Body == """{"a":1}"""), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("message.send", "sb-dev/orders-inbound", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
