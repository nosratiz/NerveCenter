using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Redrive;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Redrive;

public class StartRedriveCommandHandlerTests
{
    [Fact]
    public async Task Starts_the_move_task_and_records_a_Mutating_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.StartRedriveTaskAsync("mode=default-chain;region=eu-west-1", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", "arn:aws:sqs:eu-west-1:123456789012:orders", 10, Arg.Any<CancellationToken>())
            .Returns("task-handle-1");
        var audit = Substitute.For<IAuditScope>();

        var result = await new StartRedriveCommandHandler(operations, connections, audit)
            .HandleAsync(new StartRedriveCommand(connectionId, "aws-dev", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", "orders-dlq", "arn:aws:sqs:eu-west-1:123456789012:orders", 10));

        result.IsSuccess.Should().BeTrue();
        await audit.Received(1).RecordAsync("aws.queue.redrive", "aws-dev/orders-dlq", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.StartRedriveTaskAsync("mode=default-chain;region=eu-west-1", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", "arn:aws:sqs:eu-west-1:123456789012:orders", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("a move task is already running for this queue")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new StartRedriveCommandHandler(operations, connections, audit)
            .HandleAsync(new StartRedriveCommand(connectionId, "aws-dev", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", "orders-dlq", "arn:aws:sqs:eu-west-1:123456789012:orders", null));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.redrive", "aws-dev/orders-dlq", ActionRisk.Mutating, false, "a move task is already running for this queue", Arg.Any<CancellationToken>());
    }
}
