using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class DeleteMessageCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_message_and_records_a_Mutating_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteMessageCommandHandler(operations, connections, audit, NullLogger<DeleteMessageCommandHandler>.Instance)
            .HandleAsync(new DeleteMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", "handle-1"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-1", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.message.delete", "aws-dev/orders", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expired_receipt_handle_surfaces_the_friendly_message_and_records_failure()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.DeleteMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Amazon.SQS.Model.ReceiptHandleIsInvalidException("expired")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteMessageCommandHandler(operations, connections, audit, NullLogger<DeleteMessageCommandHandler>.Instance)
            .HandleAsync(new DeleteMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", "handle-1"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("This message's hold already expired — it's back in the queue");
        await audit.Received(1).RecordAsync("aws.message.delete", "aws-dev/orders", ActionRisk.Mutating, false,
            "This message's hold already expired — it's back in the queue", Arg.Any<CancellationToken>());
    }
}
