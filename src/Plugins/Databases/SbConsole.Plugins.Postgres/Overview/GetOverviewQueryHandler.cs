using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Overview;

public sealed class GetOverviewQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections,
    [FromKeyedServices(PostgresPlugin.PluginId)] IPluginStore store, TimeProvider clock,
    ILogger<GetOverviewQueryHandler> logger) : IScopedService
{
    private const string Description = "postgres.overview.get";

    public async Task<PluginResult<OverviewView>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        var (secret, error) = await HandlerRunner.TryResolveSecretAsync(connections, logger, connectionId, Description, ct);
        if (secret is null)
        {
            return PluginResult<OverviewView>.Fail(error ?? HandlerRunner.ConnectionNotFound);
        }

        var state = await ReadStateAsync(connectionId, ct);
        try
        {
            var overview = await operations.GetOverviewAsync(secret, ct);
            var rates = CommitRates(state.LastGood?.Overview, overview);
            await TryStoreAsync(() => OverviewSnapshotStore.WriteGoodAsync(
                store, connectionId, new OverviewSnapshot(overview, clock.GetUtcNow()), ct));
            return PluginResult<OverviewView>.Ok(new OverviewView(overview, false, null, null, null, rates));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "PostgreSQL query failed: {Description}", Description);
            var info = FriendlyPostgresError.Describe(ex);
            await TryStoreAsync(() => OverviewSnapshotStore.WriteFailureAsync(
                store, connectionId, clock.GetUtcNow(), info.Message, ct));
            var last = state.LastGood;
            return PluginResult<OverviewView>.Ok(new OverviewView(
                last?.Overview, last is not null, last?.LoadedAt, info.Message, info, new Dictionary<string, double?>()));
        }
    }

    private async Task<OverviewState> ReadStateAsync(Guid connectionId, CancellationToken ct)
    {
        try
        {
            return await OverviewSnapshotStore.ReadAsync(store, connectionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reading the PostgreSQL overview snapshot failed for {ConnectionId}.", connectionId);
            return new OverviewState(null, null, null);
        }
    }

    private async Task TryStoreAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Writing the PostgreSQL overview snapshot failed.");
        }
    }

    private static Dictionary<string, double?> CommitRates(PgOverview? previous, PgOverview current)
    {
        var rates = new Dictionary<string, double?>();
        var elapsed = previous is null ? 0 : (current.CapturedAt - previous.CapturedAt).TotalSeconds;
        var before = previous?.Databases.ToDictionary(d => d.Name, d => d.XactCommit) ?? [];
        foreach (var db in current.Databases)
        {
            double? rate = null;
            if (elapsed > 0 && before.TryGetValue(db.Name, out var prior) && db.XactCommit - prior >= 0)
            {
                rate = (db.XactCommit - prior) / elapsed;
            }

            rates[db.Name] = rate;
        }

        return rates;
    }
}
