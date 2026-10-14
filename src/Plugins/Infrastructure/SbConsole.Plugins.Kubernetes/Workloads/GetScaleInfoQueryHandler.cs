using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Workloads;

/// <summary>What the scale dialog shows before scaling: current/ready replicas, HPA ownership, per-pod requests.</summary>
public sealed class GetScaleInfoQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<GetScaleInfoQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<K8sScaleInfo>> HandleAsync(
        Guid connectionId, K8sWorkloadKind kind, string ns, string name, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.workloads.scale-info",
            secret => operations.GetScaleInfoAsync(secret, kind, ns, name, ct), ct);
}
