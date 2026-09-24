using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class ReceiveMessagesCommandHandlerTests
{
    [Fact]
    public async Task Receives_messages_and_records_a_Mutating_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var received = new[] { new ReceivedMessage("m1", "h1", "body", 1, DateTimeOffset.UtcNow, "sender", "md5", new Dictionary<string, SqsMessageAttribute>()) };
        operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", 10, 30, 20, Arg.Any<CancellationToken>())
            .Returns(received);
        var audit = Substitute.For<IAuditScope>();

        var result = await new ReceiveMessagesCommandHandler(operations, connections, audit, NullLogger<ReceiveMessagesCommandHandler>.Instance)
            .HandleAsync(new ReceiveMessagesCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", 10, 30, 20));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(received);
        await audit.Received(1).RecordAsync("aws.queue.receive", "aws-dev/orders", ActionRisk.Mutating, true, "1 message(s)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", 10, null, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ReceivedMessage>>(new InvalidOperationException("queue not found")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new ReceiveMessagesCommandHandler(operations, connections, audit, NullLogger<ReceiveMessagesCommandHandler>.Instance)
            .HandleAsync(new ReceiveMessagesCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", 10, null, 0));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.receive", "aws-dev/orders", ActionRisk.Mutating, false, "queue not found", Arg.Any<CancellationToken>());
    }
}
