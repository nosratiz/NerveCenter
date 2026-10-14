using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Namespaces;

/// <summary>Namespace names with pod counts, for <c>K8sNamespacePicker</c>.</summary>
public sealed class ListNamespacesQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<ListNamespacesQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<K8sNamespace>>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "kubernetes.namespaces.list",
            secret => operations.ListNamespacesAsync(secret, ct), ct);
}
