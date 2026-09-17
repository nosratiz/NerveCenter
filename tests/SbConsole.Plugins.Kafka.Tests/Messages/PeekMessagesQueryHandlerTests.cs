using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Messages;

public class PeekMessagesQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_peek_result_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var messages = new[] { new KafkaMessageSummary(0, 5, DateTimeOffset.UtcNow, "k", "v", false) };
        var peekResult = new PeekResult(messages, 100, 205);
        operations.PeekMessagesAsync("bootstrap.servers=real:9092", "orders", 0, PeekStart.Earliest, null, 32, Arg.Any<CancellationToken>())
            .Returns(peekResult);

        var result = await new PeekMessagesQueryHandler(operations, connections, NullLogger<PeekMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", 0, PeekStart.Earliest, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(peekResult);
    }

    [Fact]
    public async Task Zero_messages_still_returns_the_real_watermarks()
    {
        // Guards the "poll timeout mistaken for end-of-partition" bug at the handler level: a
        // topic that genuinely has no messages to return in this window must still report valid,
        // non-zero watermarks, not an empty/zeroed-out result.
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var peekResult = new PeekResult([], 100, 100_205);
        operations.PeekMessagesAsync("bootstrap.servers=real:9092", "orders", 0, PeekStart.Latest, null, 32, Arg.Any<CancellationToken>())
            .Returns(peekResult);

        var result = await new PeekMessagesQueryHandler(operations, connections, NullLogger<PeekMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", 0, PeekStart.Latest, null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Messages.Should().BeEmpty();
        result.Value.LowWatermark.Should().Be(100);
        result.Value.HighWatermark.Should().Be(100_205);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_result()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new PeekMessagesQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<PeekMessagesQueryHandler>.Instance)
            .HandleAsync(Guid.NewGuid(), "orders", 0, PeekStart.Earliest, null);

        result.IsSuccess.Should().BeFalse();
    }
}
