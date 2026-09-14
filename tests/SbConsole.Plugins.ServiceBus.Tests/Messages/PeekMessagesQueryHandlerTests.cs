using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class PeekMessagesQueryHandlerTests
{
    [Fact]
    public async Task Peeks_the_dead_letter_subqueue_when_asked()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var messages = new[] { new PeekedMessage(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>(), "MaxDeliveryCountExceeded", "boom") };
        operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", true, 32, null, Arg.Any<CancellationToken>()).Returns(messages);

        var result = await new PeekMessagesQueryHandler(operations, connections, NullLogger<PeekMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders-inbound", fromDeadLetter: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(messages);
    }

    [Fact]
    public async Task Unknown_connection_fails_rather_than_returning_an_empty_page()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new PeekMessagesQueryHandler(Substitute.For<IServiceBusOperations>(), connections, NullLogger<PeekMessagesQueryHandler>.Instance)
            .HandleAsync(Guid.NewGuid(), "orders-inbound", fromDeadLetter: false);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.PeekMessagesAsync("Endpoint=sb://real", "orders-inbound", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PeekedMessage>>(new InvalidOperationException("namespace unreachable")));

        var result = await new PeekMessagesQueryHandler(operations, connections, NullLogger<PeekMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders-inbound", fromDeadLetter: false);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("namespace unreachable");
    }
}
