using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class CreateQueueCommandHandlerTests
{
    private static CreateQueueRequest StandardRequest(string name) =>
        new(name, false, 30, 345600, 0, 262144, 20, null, null, null, null, null);

    [Fact]
    public async Task Creates_the_queue_and_records_a_successful_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var request = StandardRequest("orders");
        operations.CreateQueueAsync("mode=default-chain;region=eu-west-1", request, Arg.Any<CancellationToken>())
            .Returns("https://sqs.eu-west-1.amazonaws.com/123456789012/orders");
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new CreateQueueCommand(connectionId, "aws-dev", request));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("https://sqs.eu-west-1.amazonaws.com/123456789012/orders");
        await audit.Received(1).RecordAsync("aws.queue.create", "aws-dev/orders", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_and_records_no_audit_entry()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(Substitute.For<ISqsOperations>(), connections, audit)
            .HandleAsync(new CreateQueueCommand(Guid.NewGuid(), "aws-dev", StandardRequest("orders")));

        result.IsSuccess.Should().BeFalse();
        await audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, ct: default);
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry_with_the_friendly_message()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var request = StandardRequest("orders");
        operations.CreateQueueAsync("mode=default-chain;region=eu-west-1", request, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("region unreachable")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new CreateQueueCommand(connectionId, "aws-dev", request));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.create", "aws-dev/orders", ActionRisk.Mutating, false, "region unreachable", Arg.Any<CancellationToken>());
    }
}
