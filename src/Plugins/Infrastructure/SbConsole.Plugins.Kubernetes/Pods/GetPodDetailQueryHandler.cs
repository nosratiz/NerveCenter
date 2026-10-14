using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Pods;

/// <summary>Everything the Pod detail page shows (mockup 3d); a null value means the pod no longer exists.</summary>
public sealed class GetPodDetailQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<GetPodDetailQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<K8sPodDetail?>> HandleAsync(Guid connectionId, string ns, string name, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.pod.detail",
            secret => operations.GetPodDetailAsync(secret, ns, name, ct), ct);
}
