using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class PurgeQueueCommandHandlerTests
{
    [Fact]
    public async Task Purges_the_queue_and_records_a_Destructive_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new PurgeQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new PurgeQueueCommand(connectionId, "aws-dev", "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).PurgeQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.queue.purge", "aws-dev/orders", ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_Destructive_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.PurgeQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("purge already in progress")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new PurgeQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new PurgeQueueCommand(connectionId, "aws-dev", "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.purge", "aws-dev/orders", ActionRisk.Destructive, false, "purge already in progress", Arg.Any<CancellationToken>());
    }
}
