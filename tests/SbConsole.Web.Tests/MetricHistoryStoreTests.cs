using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using SbConsole.Sdk;

namespace SbConsole.Web.Tests;

public class MetricHistoryStoreTests
{
    private readonly IPluginStore _store = Substitute.For<IPluginStore>();
    private readonly Guid _connectionId = Guid.NewGuid();

    [Fact]
    public async Task Returns_an_empty_list_when_no_history_exists()
    {
        _store.GetAsync(MetricHistoryKey.For(_connectionId, "orders-inbound"), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var points = await MetricHistoryStore.ReadAsync(_store, _connectionId, "orders-inbound");

        points.Should().BeEmpty();
    }

    [Fact]
    public async Task Round_trips_points_written_by_AppendAsync()
    {
        var point = new MetricSnapshotPoint(DateTimeOffset.Parse("2026-09-14T12:00:00Z"), 12, 3);
        _store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        await MetricHistoryStore.AppendAsync(_store, _connectionId, "orders-inbound", point);
        var written = (string)Capture();
        _store.GetAsync(MetricHistoryKey.For(_connectionId, "orders-inbound"), Arg.Any<CancellationToken>())
            .Returns(written);

        var points = await MetricHistoryStore.ReadAsync(_store, _connectionId, "orders-inbound");

        points.Should().ContainSingle().Which.Should().Be(point);

        object Capture()
        {
            var call = _store.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "SetAsync");
            return call.GetArguments()[1]!;
        }
    }
}
