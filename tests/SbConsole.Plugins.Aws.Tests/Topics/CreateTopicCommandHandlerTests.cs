using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class CreateTopicCommandHandlerTests
{
    [Fact]
    public async Task Creates_and_audits_as_mutating()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var request = new CreateTopicRequest("order-events-topic", false, null, null);
        operations.CreateTopicAsync("mode=access-keys;region=us-east-1", request, Arg.Any<CancellationToken>())
            .Returns("arn:aws:sns:us-east-1:1:order-events-topic");
        var handler = new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance);

        var result = await handler.HandleAsync(new CreateTopicCommand(connectionId, "aws-dev", request));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("arn:aws:sns:us-east-1:1:order-events-topic");
        await audit.Received(1).RecordAsync("aws.topic.create", "aws-dev/order-events-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_fails()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var handler = new CreateTopicCommandHandler(Substitute.For<ISnsOperations>(), connections, Substitute.For<IAuditScope>(), NullLogger<CreateTopicCommandHandler>.Instance);

        var result = await handler.HandleAsync(new CreateTopicCommand(Guid.NewGuid(), "aws-dev", new CreateTopicRequest("x", false, null, null)));

        result.IsSuccess.Should().BeFalse();
    }
}
