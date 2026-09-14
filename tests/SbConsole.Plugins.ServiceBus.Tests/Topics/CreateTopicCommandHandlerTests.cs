using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Topics;

public class CreateTopicCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_topic_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance)
            .HandleAsync(new CreateTopicCommand(connectionId, "sb-dev", "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateTopicAsync("Endpoint=sb://real", Arg.Is<CreateTopicRequest>(r => r.Name == "orders"), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("topic.create", "sb-dev/orders", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.CreateTopicAsync(Arg.Any<string>(), Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance)
            .HandleAsync(new CreateTopicCommand(connectionId, "sb-dev", "orders"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already exists");
        await audit.Received(1).RecordAsync("topic.create", "sb-dev/orders", ActionRisk.Mutating, false, "already exists", Arg.Any<CancellationToken>());
    }
}
