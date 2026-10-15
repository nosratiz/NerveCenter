using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Nodes;

/// <summary>The pods scheduled on one node, across all namespaces.</summary>
public sealed class ListNodePodsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<ListNodePodsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<K8sPodSummary>>> HandleAsync(Guid connectionId, string node, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.node.pods.list",
            secret => operations.ListPodsOnNodeAsync(secret, node, ct), ct);
}
