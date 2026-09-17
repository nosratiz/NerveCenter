using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Topics;

public class DeleteTopicCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_topic_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteTopicCommandHandler(operations, connections, audit, NullLogger<DeleteTopicCommandHandler>.Instance)
            .HandleAsync(new DeleteTopicCommand(connectionId, "kafka-dev", false, "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteTopicAsync("bootstrap.servers=real:9092", "orders", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("topic.delete", "kafka-dev/orders", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kafka_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.DeleteTopicAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("not found")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteTopicCommandHandler(operations, connections, audit, NullLogger<DeleteTopicCommandHandler>.Instance)
            .HandleAsync(new DeleteTopicCommand(connectionId, "kafka-dev", false, "orders"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not found");
        await audit.Received(1).RecordAsync("topic.delete", "kafka-dev/orders", ActionRisk.Destructive, false, "not found", Arg.Any<CancellationToken>());
    }
}
