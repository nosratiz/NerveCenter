using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Topics;

public class DeleteTopicCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_topic_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteTopicCommandHandler(operations, connections, audit, NullLogger<DeleteTopicCommandHandler>.Instance)
            .HandleAsync(new DeleteTopicCommand(connectionId, "sb-dev", false, "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteTopicAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("topic.delete", "sb-dev/orders", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
