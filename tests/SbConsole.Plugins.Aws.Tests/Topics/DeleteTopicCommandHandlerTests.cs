using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class DeleteTopicCommandHandlerTests
{
    [Fact]
    public async Task Deletes_and_audits_as_destructive()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var handler = new DeleteTopicCommandHandler(operations, connections, audit, NullLogger<DeleteTopicCommandHandler>.Instance);

        var result = await handler.HandleAsync(new DeleteTopicCommand(connectionId, "aws-dev", "arn:aws:sns:us-east-1:1:order-events-topic", "order-events-topic"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteTopicAsync("mode=access-keys;region=us-east-1", "arn:aws:sns:us-east-1:1:order-events-topic", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.topic.delete", "aws-dev/order-events-topic", ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
    }
}
