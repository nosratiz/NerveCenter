using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Streams;

namespace SbConsole.Plugins.Redis.Tests.Handlers;

public class StreamsQueryHandlerTests : HandlerTestBase
{
    private const string Key = "orders:events";

    private StreamsQueryHandler Handler => new(Operations, Connections, Log<StreamsQueryHandler>());

    [Fact]
    public async Task Scan_returns_the_page_from_operations()
    {
        var page = new StreamScanPage([new StreamSummary(Key, 12, "1696000000123-0", "1695000000000-0", "1696000000123-0")], 0, true, 1000, 2000);
        var request = new ScanStreamsRequest(Database, "orders:*", 0, 1000);
        Operations.ScanStreamsAsync(Secret, request, Arg.Any<CancellationToken>()).Returns(page);

        var result = await Handler.ScanAsync(ConnectionId, request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(page);
    }

    [Fact]
    public async Task Scan_failure_is_friendly()
    {
        var request = new ScanStreamsRequest(Database, "orders:*", 0, 1000);
        Operations.ScanStreamsAsync(Secret, request, Arg.Any<CancellationToken>()).ThrowsAsync(Timeout);

        var result = await Handler.ScanAsync(ConnectionId, request);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TimeoutText);
    }

    [Fact]
    public async Task Missing_connection_fails_without_a_redis_call()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler.GetOverviewAsync(ConnectionId, Database, Key);

        result.Error.Should().Be("Connection not found.");
        Operations.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task GetOverview_returns_the_overview_from_operations()
    {
        var overview = new StreamOverview(Key, 12, 1, 1, 1, "1696000000123-0", "1695000000000-0", "1696000000123-0");
        Operations.GetStreamOverviewAsync(Secret, Database, Key, Arg.Any<CancellationToken>()).Returns(overview);

        var result = await Handler.GetOverviewAsync(ConnectionId, Database, Key);

        result.Value.Should().Be(overview);
    }

    [Fact]
    public async Task GetConsumerGroups_returns_the_groups_from_operations()
    {
        IReadOnlyList<StreamConsumerGroupSummary> groups = [new StreamConsumerGroupSummary("workers", 2, 3, "1696000000000-0", 0)];
        Operations.GetStreamConsumerGroupsAsync(Secret, Database, Key, Arg.Any<CancellationToken>()).Returns(groups);

        var result = await Handler.GetConsumerGroupsAsync(ConnectionId, Database, Key);

        result.Value.Should().BeSameAs(groups);
    }

    [Fact]
    public async Task GetConsumers_returns_the_consumers_from_operations()
    {
        IReadOnlyList<StreamConsumer> consumers = [new StreamConsumer("worker-1", 3, TimeSpan.FromSeconds(30))];
        Operations.GetStreamConsumersAsync(Secret, Database, Key, "workers", Arg.Any<CancellationToken>()).Returns(consumers);

        var result = await Handler.GetConsumersAsync(ConnectionId, Database, Key, "workers");

        result.Value.Should().BeSameAs(consumers);
    }

    [Fact]
    public async Task GetPendingEntries_returns_the_entries_from_operations()
    {
        IReadOnlyList<StreamPendingEntry> pending = [new StreamPendingEntry("1696000000000-0", "worker-1", TimeSpan.FromSeconds(30), 2)];
        Operations.GetStreamPendingEntriesAsync(Secret, Database, Key, "workers", 50, Arg.Any<CancellationToken>()).Returns(pending);

        var result = await Handler.GetPendingEntriesAsync(ConnectionId, Database, Key, "workers", 50);

        result.Value.Should().BeSameAs(pending);
    }

    [Fact]
    public async Task GetEntries_returns_the_entries_from_operations()
    {
        IReadOnlyList<StreamEntry> entries = [new StreamEntry("1696000000123-0", [new StreamFieldValue("event", "order.created")])];
        Operations.GetStreamEntriesAsync(Secret, Database, Key, null, 20, Arg.Any<CancellationToken>()).Returns(entries);

        var result = await Handler.GetEntriesAsync(ConnectionId, Database, Key, null, 20);

        result.Value.Should().BeSameAs(entries);
    }
}
