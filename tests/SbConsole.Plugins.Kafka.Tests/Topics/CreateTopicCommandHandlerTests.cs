using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Topics;

public class CreateTopicCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_topic_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance)
            .HandleAsync(new CreateTopicCommand(connectionId, "kafka-dev", "orders", 3, 1));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateTopicAsync("bootstrap.servers=real:9092", Arg.Is<CreateTopicRequest>(r => r.Name == "orders" && r.PartitionCount == 3 && r.ReplicationFactor == 1), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("topic.create", "kafka-dev/orders", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kafka_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.CreateTopicAsync(Arg.Any<string>(), Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance)
            .HandleAsync(new CreateTopicCommand(connectionId, "kafka-dev", "orders", 3, 1));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already exists");
        await audit.Received(1).RecordAsync("topic.create", "kafka-dev/orders", ActionRisk.Mutating, false, "already exists", Arg.Any<CancellationToken>());
    }
}
