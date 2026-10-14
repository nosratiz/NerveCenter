using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Config;

/// <summary>ConfigMaps in the selected namespaces (empty = all) with who uses each.</summary>
public sealed class ListConfigMapsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<ListConfigMapsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<ConfigMapRow>>> HandleAsync(Guid connectionId, IReadOnlyList<string> namespaces, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.configmaps.list",
            async secret => ConfigMapUsage.Resolve(await operations.ListConfigMapsAsync(secret, namespaces, ct)), ct);
}
