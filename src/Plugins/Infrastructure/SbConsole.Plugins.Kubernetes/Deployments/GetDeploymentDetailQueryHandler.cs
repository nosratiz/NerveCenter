using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Deployments;

/// <summary>Everything the Deployment detail page shows (mockup 3c); a null value means the Deployment no longer exists.</summary>
public sealed class GetDeploymentDetailQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<GetDeploymentDetailQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<K8sDeploymentDetail?>> HandleAsync(Guid connectionId, string ns, string name, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.deployment.detail",
            secret => operations.GetDeploymentDetailAsync(secret, ns, name, ct), ct);
}
