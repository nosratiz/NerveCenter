using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class DeleteQueueCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_queue_and_records_a_Destructive_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteQueueCommandHandler(operations, connections, audit, NullLogger<DeleteQueueCommandHandler>.Instance)
            .HandleAsync(new DeleteQueueCommand(connectionId, "aws-dev", "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.queue.delete", "aws-dev/orders", ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_and_records_no_audit_entry()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteQueueCommandHandler(Substitute.For<ISqsOperations>(), connections, audit, NullLogger<DeleteQueueCommandHandler>.Instance)
            .HandleAsync(new DeleteQueueCommand(Guid.NewGuid(), "aws-dev", "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeFalse();
        await audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, ct: default);
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_Destructive_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.DeleteQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("queue not found")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteQueueCommandHandler(operations, connections, audit, NullLogger<DeleteQueueCommandHandler>.Instance)
            .HandleAsync(new DeleteQueueCommand(connectionId, "aws-dev", "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.delete", "aws-dev/orders", ActionRisk.Destructive, false, "queue not found", Arg.Any<CancellationToken>());
    }
}
