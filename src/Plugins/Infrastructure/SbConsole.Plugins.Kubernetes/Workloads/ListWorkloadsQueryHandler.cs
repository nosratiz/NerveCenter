using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Workloads;

/// <summary>Workloads of one kind in the selected namespaces (empty = all), optionally filtered by a label selector.</summary>
public sealed class ListWorkloadsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<ListWorkloadsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<K8sWorkload>>> HandleAsync(
        Guid connectionId, K8sWorkloadKind kind, IReadOnlyList<string> namespaces, string? labelSelector, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.workloads.list",
            secret => operations.ListWorkloadsAsync(secret, kind, namespaces, labelSelector, ct), ct);
}
