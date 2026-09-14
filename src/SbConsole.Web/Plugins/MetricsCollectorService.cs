using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

/// <summary>
/// Periodically asks every plugin for a point-in-time reading of its resources (IPlugin.
/// GetResourceMetricsAsync) and appends each reading to that resource's history under the
/// plugin's own IPluginStore, so a plugin's UI can read it back to draw trend sparklines (e.g.
/// the Queues page's Active/DLQ columns). A true background service (not a client-side loop like
/// NavMenu's badge refresh) because history must keep accumulating even while no browser circuit
/// is connected.
/// </summary>
public sealed class MetricsCollectorService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<MetricsCollectorService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Best-effort: a failed tick must not stop future ticks from collecting metrics.
                logger.LogWarning(ex, "Collecting resource metrics failed for this tick; trying again next tick.");
            }

            try
            {
                await Task.Delay(TickInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Public so tests can call one tick directly instead of waiting on the real timer (same
    // testability convention as NavMenu.RefreshBadgesAsync).
    public async Task TickAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionProvider>();
        var storeFactory = scope.ServiceProvider.GetRequiredService<IPluginStoreFactory>();

        foreach (var plugin in registry.Plugins)
        {
            var store = storeFactory.For(plugin.Id);
            var pluginConnections = await connections.ListAsync(plugin.ConnectionKind, ct);
            foreach (var connection in pluginConnections)
            {
                try
                {
                    // GetSecretAsync inside the try, not before it -- see NavMenu.RefreshBadgesAsync's
                    // identical comment: a throw here must not stop the rest of this tick.
                    var secret = await connections.GetSecretAsync(connection.Id, ct);
                    if (secret is null)
                    {
                        continue;
                    }

                    var metrics = await plugin.GetResourceMetricsAsync(secret, ct);
                    foreach (var metric in metrics)
                    {
                        var point = new MetricSnapshotPoint(clock.GetUtcNow(), metric.ActiveCount, metric.DeadLetterCount);
                        await MetricHistoryStore.AppendAsync(store, connection.Id, metric.ResourceName, point, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Collecting resource metrics for connection {ConnectionId} failed; skipping it.", connection.Id);
                }
            }
        }
    }
}
