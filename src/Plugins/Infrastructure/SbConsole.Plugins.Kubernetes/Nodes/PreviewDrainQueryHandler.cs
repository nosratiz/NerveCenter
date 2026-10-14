using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Nodes;

/// <summary>The drain dialog's eviction preview: reads the node's pods, PDBs and owner replicas and plans. Changes nothing.</summary>
public sealed class PreviewDrainQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<PreviewDrainQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<DrainPlan>> HandleAsync(Guid connectionId, string node, int gracePeriodSeconds, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.node.drain.preview",
            async secret => DrainPlanner.Plan(await operations.GetDrainInputsAsync(secret, node, ct), gracePeriodSeconds), ct);
}
