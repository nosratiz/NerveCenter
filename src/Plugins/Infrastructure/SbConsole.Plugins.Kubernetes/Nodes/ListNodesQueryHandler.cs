using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Nodes;

/// <summary>Every node with its requests (NotReady first, then by name). A 403 reads "Your account can't list nodes."</summary>
public sealed class ListNodesQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<ListNodesQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<K8sNodeInfo>>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.nodes.list",
            secret => operations.ListNodesAsync(secret, ct), ct);
}
