using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class SendMessageCommandHandlerTests
{
    [Fact]
    public async Task Sends_the_message_and_records_a_Mutating_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var request = new SendMessageRequest("hello", null, null, null, null);
        var audit = Substitute.For<IAuditScope>();

        var result = await new SendMessageCommandHandler(operations, connections, audit, NullLogger<SendMessageCommandHandler>.Instance)
            .HandleAsync(new SendMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", request));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).SendMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", request, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.message.send", "aws-dev/orders", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var request = new SendMessageRequest("hello", null, null, null, null);
        operations.SendMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", request, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("message too large")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new SendMessageCommandHandler(operations, connections, audit, NullLogger<SendMessageCommandHandler>.Instance)
            .HandleAsync(new SendMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", request));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.message.send", "aws-dev/orders", ActionRisk.Mutating, false, "message too large", Arg.Any<CancellationToken>());
    }
}
