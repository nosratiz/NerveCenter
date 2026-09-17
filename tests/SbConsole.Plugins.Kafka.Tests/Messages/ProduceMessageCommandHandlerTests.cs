using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Messages;

public class ProduceMessageCommandHandlerTests
{
    [Fact]
    public async Task Produces_the_message_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ProduceMessageCommandHandler(operations, connections, audit, NullLogger<ProduceMessageCommandHandler>.Instance)
            .HandleAsync(new ProduceMessageCommand(connectionId, "kafka-dev", "orders", "my-key", "my-value", null));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).ProduceMessageAsync("bootstrap.servers=real:9092", "orders", "my-key", "my-value", null, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("kafka.message.produce", "kafka-dev/orders", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kafka_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ProduceMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("unknown topic")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new ProduceMessageCommandHandler(operations, connections, audit, NullLogger<ProduceMessageCommandHandler>.Instance)
            .HandleAsync(new ProduceMessageCommand(connectionId, "kafka-dev", "orders", null, "my-value", null));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("unknown topic");
        await audit.Received(1).RecordAsync("kafka.message.produce", "kafka-dev/orders", ActionRisk.Mutating, false, "unknown topic", Arg.Any<CancellationToken>());
    }
}
