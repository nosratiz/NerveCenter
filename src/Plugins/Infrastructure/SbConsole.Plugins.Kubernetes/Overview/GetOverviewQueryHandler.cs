using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Overview;

/// <summary>What the Overview page renders: a fresh overview, or the last good one marked stale, plus the error.</summary>
public sealed record OverviewView(K8sOverview? Overview, bool IsStale, DateTimeOffset? StaleSince, K8sErrorInfo? Error);

/// <summary>
/// Loads the overview and keeps the last good one in the plugin store (Postgres pattern). A failure returns the
/// last good overview as stale — but only when it was captured for the same namespace selection.
/// </summary>
public sealed class GetOverviewQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections,
    [FromKeyedServices(KubernetesPlugin.PluginId)] IPluginStore store, TimeProvider clock,
    ILogger<GetOverviewQueryHandler> logger) : IScopedService
{
    private const string Description = "k8s.overview.get";

    public async Task<PluginResult<OverviewView>> HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default)
    {
        var (secret, error) = await HandlerRunner.TryResolveSecretAsync(connections, logger, connectionId, Description, ct);
        if (secret is null)
        {
            return PluginResult<OverviewView>.Fail(error ?? HandlerRunner.ConnectionNotFound);
        }

        var selection = Normalize(namespaces);
        try
        {
            var overview = await operations.GetOverviewAsync(secret, namespaces, ct);
            await TryStoreAsync(() => OverviewSnapshotStore.WriteGoodAsync(
                store, connectionId, new OverviewSnapshot(overview, clock.GetUtcNow(), selection), ct));
            return PluginResult<OverviewView>.Ok(new OverviewView(overview, false, null, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Kubernetes query failed: {Description}", Description);
            var info = FriendlyKubernetesError.Describe(ex);
            var state = await ReadStateAsync(connectionId, ct);
            await TryStoreAsync(() => OverviewSnapshotStore.WriteFailureAsync(
                store, connectionId, clock.GetUtcNow(), info.Message, ct));
            var last = state.LastGood is { } good && Normalize(good.Namespaces).SequenceEqual(selection, StringComparer.Ordinal)
                ? good
                : null;
            return PluginResult<OverviewView>.Ok(new OverviewView(last?.Overview, last is not null, last?.LoadedAt, info));
        }
    }

    private static List<string> Normalize(IReadOnlyList<string>? namespaces) =>
        (namespaces ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private async Task<OverviewState> ReadStateAsync(Guid connectionId, CancellationToken ct)
    {
        try
        {
            return await OverviewSnapshotStore.ReadAsync(store, connectionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reading the Kubernetes overview snapshot failed for {ConnectionId}.", connectionId);
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
            logger.LogWarning(ex, "Writing the Kubernetes overview snapshot failed.");
        }
    }
}
