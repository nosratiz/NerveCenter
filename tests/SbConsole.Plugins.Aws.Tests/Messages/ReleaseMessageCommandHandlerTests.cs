using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class ReleaseMessageCommandHandlerTests
{
    [Fact]
    public async Task Releases_the_message_by_zeroing_visibility_timeout_and_records_audit()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ReleaseMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new ReleaseMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", "handle-1"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).ChangeMessageVisibilityAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-1", 0, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.message.release", "aws-dev/orders", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.ChangeMessageVisibilityAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-1", 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("handle expired")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new ReleaseMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new ReleaseMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", "handle-1"));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.message.release", "aws-dev/orders", ActionRisk.Mutating, false, "handle expired", Arg.Any<CancellationToken>());
    }
}
