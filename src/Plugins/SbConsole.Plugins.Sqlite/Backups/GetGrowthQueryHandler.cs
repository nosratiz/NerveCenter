using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>Samples oldest first, their summary, and the live file's volume (null when unknown).</summary>
public sealed record GrowthOverview(IReadOnlyList<GrowthSample> Samples, GrowthSummary Summary, VolumeInfo? Volume);

/// <summary>
/// The Overview's "Growth — 30 days" card (design spec §11.8). Every load also takes a sample
/// (at most one an hour, like the scheduler) -- the only write a query makes, and only to the
/// plugin's own sample history, so it is not audited. Sampling or volume failures are logged and
/// leave the card with what is stored.
/// </summary>
public sealed class GetGrowthQueryHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<GetGrowthQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<GrowthOverview>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "sqlite.growth.get", async secret =>
        {
            try
            {
                await GrowthSampler.SampleIfDueAsync(operations, store, connectionId, secret, clock.GetUtcNow(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Taking a growth sample for connection {ConnectionId} failed.", connectionId);
            }

            VolumeInfo? volume = null;
            try
            {
                volume = await operations.GetVolumeAsync(secret, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Reading the volume for connection {ConnectionId} failed.", connectionId);
            }

            var samples = await GrowthStore.ReadAsync(store, connectionId, ct);
            return new GrowthOverview(samples, GrowthSummary.From(samples, volume), volume);
        }, ct);
}
