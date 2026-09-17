using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.ConsumerGroups;

public class ResetConsumerGroupOffsetCommandHandlerTests
{
    [Fact]
    public async Task Resets_the_offset_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResetConsumerGroupOffsetCommandHandler(operations, connections, audit, NullLogger<ResetConsumerGroupOffsetCommandHandler>.Instance)
            .HandleAsync(new ResetConsumerGroupOffsetCommand(connectionId, "kafka-dev", "order-processors", "orders", 2, OffsetResetMode.Earliest, null, null));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).ResetConsumerGroupOffsetAsync(
            "bootstrap.servers=real:9092", "order-processors", "orders", 2, OffsetResetMode.Earliest, null, null, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("kafka.consumergroup.resetoffset", "kafka-dev/order-processors/orders-2", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Offset_mode_without_an_offset_value_fails_before_calling_the_operation()
    {
        var connections = Substitute.For<IConnectionProvider>();
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResetConsumerGroupOffsetCommandHandler(operations, connections, audit, NullLogger<ResetConsumerGroupOffsetCommandHandler>.Instance)
            .HandleAsync(new ResetConsumerGroupOffsetCommand(Guid.NewGuid(), "kafka-dev", "order-processors", "orders", 2, OffsetResetMode.Offset, null, null));

        result.IsSuccess.Should().BeFalse();
        await operations.DidNotReceive().ResetConsumerGroupOffsetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OffsetResetMode>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Timestamp_mode_without_a_timestamp_fails_before_calling_the_operation()
    {
        var connections = Substitute.For<IConnectionProvider>();
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResetConsumerGroupOffsetCommandHandler(operations, connections, audit, NullLogger<ResetConsumerGroupOffsetCommandHandler>.Instance)
            .HandleAsync(new ResetConsumerGroupOffsetCommand(Guid.NewGuid(), "kafka-dev", "order-processors", "orders", 2, OffsetResetMode.Timestamp, null, null));

        result.IsSuccess.Should().BeFalse();
        await operations.DidNotReceive().ResetConsumerGroupOffsetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OffsetResetMode>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kafka_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ResetConsumerGroupOffsetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OffsetResetMode>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("group has active members")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResetConsumerGroupOffsetCommandHandler(operations, connections, audit, NullLogger<ResetConsumerGroupOffsetCommandHandler>.Instance)
            .HandleAsync(new ResetConsumerGroupOffsetCommand(connectionId, "kafka-dev", "order-processors", "orders", 2, OffsetResetMode.Latest, null, null));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("group has active members");
        await audit.Received(1).RecordAsync("kafka.consumergroup.resetoffset", "kafka-dev/order-processors/orders-2", ActionRisk.Destructive, false, "group has active members", Arg.Any<CancellationToken>());
    }
}
