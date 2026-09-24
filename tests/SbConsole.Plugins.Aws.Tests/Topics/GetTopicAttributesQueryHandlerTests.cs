using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class GetTopicAttributesQueryHandlerTests
{
    private const string Secret = "mode=access-keys;region=us-east-1";
    private const string TopicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";

    [Fact]
    public async Task Returns_the_attributes_from_operations()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        operations.GetTopicAttributesAsync(Secret, TopicArn, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["DisplayName"] = "Shipments", ["Owner"] = "1" });
        var handler = new GetTopicAttributesQueryHandler(operations, connections, NullLogger<GetTopicAttributesQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId, TopicArn);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("DisplayName", "Shipments").And.Contain("Owner", "1");
    }

    [Fact]
    public async Task Fails_when_the_connection_is_not_found()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var handler = new GetTopicAttributesQueryHandler(operations, connections, NullLogger<GetTopicAttributesQueryHandler>.Instance);

        var result = await handler.HandleAsync(Guid.NewGuid(), TopicArn);

        result.IsSuccess.Should().BeFalse();
        await operations.DidNotReceive().GetTopicAttributesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_denied_call_fails_without_throwing()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        operations.GetTopicAttributesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyDictionary<string, string>>(new InvalidOperationException("AccessDenied")));
        var handler = new GetTopicAttributesQueryHandler(operations, connections, NullLogger<GetTopicAttributesQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId, TopicArn);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
