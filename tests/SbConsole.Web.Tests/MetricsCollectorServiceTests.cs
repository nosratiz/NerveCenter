using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Sdk;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

public class MetricsCollectorServiceTests
{
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IPluginStoreFactory _storeFactory = Substitute.For<IPluginStoreFactory>();
    private readonly IPluginStore _store = Substitute.For<IPluginStore>();
    private readonly IPlugin _plugin = Substitute.For<IPlugin>();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-14T12:00:00Z"));
    private readonly Guid _connectionId = Guid.NewGuid();

    private MetricsCollectorService BuildService()
    {
        _plugin.Id.Returns("fake");
        _plugin.ConnectionKind.Returns("fake");
        _storeFactory.For("fake").Returns(_store);

        var services = new ServiceCollection();
        services.AddSingleton(new PluginRegistry([_plugin]));
        services.AddSingleton(_connections);
        services.AddSingleton(_storeFactory);
        var provider = services.BuildServiceProvider();

        return new MetricsCollectorService(provider.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<MetricsCollectorService>.Instance);
    }

    [Fact]
    public async Task Appends_a_snapshot_point_for_each_resource_the_plugin_reports()
    {
        _connections.ListAsync("fake", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, "fake-conn", "fake", []) });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("secret");
        _plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric> { new("orders-inbound", 12, 3) });
        _store.GetAsync(MetricHistoryKey.For(_connectionId, "orders-inbound"), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var service = BuildService();
        await service.TickAsync();

        await _store.Received(1).SetAsync(
            MetricHistoryKey.For(_connectionId, "orders-inbound"),
            Arg.Is<string>(json => JsonSerializer.Deserialize<List<MetricSnapshotPoint>>(json)!.Single()
                == new MetricSnapshotPoint(_clock.GetUtcNow(), 12, 3)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prunes_points_older_than_the_retention_window_and_keeps_the_rest()
    {
        _connections.ListAsync("fake", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, "fake-conn", "fake", []) });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("secret");
        _plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric> { new("orders-inbound", 5, 0) });

        var stale = new MetricSnapshotPoint(_clock.GetUtcNow().AddHours(-25), 99, 99);
        var recent = new MetricSnapshotPoint(_clock.GetUtcNow().AddHours(-1), 7, 1);
        _store.GetAsync(MetricHistoryKey.For(_connectionId, "orders-inbound"), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(new List<MetricSnapshotPoint> { stale, recent }));

        var service = BuildService();
        await service.TickAsync();

        await _store.Received(1).SetAsync(
            MetricHistoryKey.For(_connectionId, "orders-inbound"),
            Arg.Is<string>(json => JsonSerializer.Deserialize<List<MetricSnapshotPoint>>(json)!
                .SequenceEqual(new List<MetricSnapshotPoint> { recent, new(_clock.GetUtcNow(), 5, 0) })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_connection_whose_secret_is_missing_is_skipped_not_fatal()
    {
        _connections.ListAsync("fake", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, "fake-conn", "fake", []) });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var service = BuildService();
        var act = () => service.TickAsync();

        await act.Should().NotThrowAsync();
        await _plugin.DidNotReceive().GetResourceMetricsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_plugin_that_throws_does_not_stop_the_tick()
    {
        _connections.ListAsync("fake", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, "fake-conn", "fake", []) });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("secret");
        _plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PluginResourceMetric>>(new InvalidOperationException("boom")));

        var service = BuildService();
        var act = () => service.TickAsync();

        await act.Should().NotThrowAsync();
    }
}
