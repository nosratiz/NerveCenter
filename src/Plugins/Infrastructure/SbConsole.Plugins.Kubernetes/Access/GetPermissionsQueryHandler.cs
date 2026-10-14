using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Access;

public sealed class GetPermissionsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, AccessCache cache,
    ILogger<GetPermissionsQueryHandler> logger) : IScopedService
{
    /// <summary>
    /// Cached unless <paramref name="refresh"/>. A failed probe never blocks a page: it logs and returns
    /// <see cref="K8sPermissions.All"/> (not cached), leaving the server's 403 as the backstop.
    /// </summary>
    public Task<PluginResult<K8sPermissions>> HandleAsync(
        Guid connectionId, IReadOnlyList<string> namespaces, bool refresh = false, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "kubernetes.permissions.get",
            async secret =>
            {
                if (!refresh && cache.TryGet(connectionId, namespaces, out var cached))
                {
                    return cached;
                }

                try
                {
                    var permissions = K8sPermissions.From(await operations.GetAccessAsync(secret, namespaces, ct));
                    cache.Set(connectionId, namespaces, permissions);
                    return permissions;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Kubernetes access probe failed for {ConnectionId}; showing all controls.", connectionId);
                    return K8sPermissions.All;
                }
            }, ct);
}
