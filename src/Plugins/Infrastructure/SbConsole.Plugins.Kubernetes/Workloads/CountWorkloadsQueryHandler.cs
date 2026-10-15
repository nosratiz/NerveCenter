using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Workloads;

/// <summary>Per-kind workload counts (and whether any is unhealthy) for the Workloads page tabs.</summary>
public sealed class CountWorkloadsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<CountWorkloadsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyDictionary<K8sWorkloadKind, K8sKindCount>>> HandleAsync(
        Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.workloads.count",
            secret => operations.CountWorkloadsAsync(secret, namespaces, ct), ct);
}
