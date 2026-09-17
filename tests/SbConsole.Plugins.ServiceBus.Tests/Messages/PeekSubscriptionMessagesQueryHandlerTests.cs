using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class PeekSubscriptionMessagesQueryHandlerTests
{
    [Fact]
    public async Task Returns_peeked_messages_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var messages = new[] { new PeekedMessage(1, "{}", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()) };
        operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", false, 32, null, Arg.Any<CancellationToken>()).Returns(messages);

        var result = await new PeekSubscriptionMessagesQueryHandler(operations, connections, NullLogger<PeekSubscriptionMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team", false);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(messages);
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PeekedMessage>>(new InvalidOperationException("subscription not found")));

        var result = await new PeekSubscriptionMessagesQueryHandler(operations, connections, NullLogger<PeekSubscriptionMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team", true);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("subscription not found");
    }
}
