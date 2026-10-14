using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Workloads;

/// <summary>A resource as YAML (managedFields stripped); a null value means it no longer exists.</summary>
public sealed class GetResourceYamlQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<GetResourceYamlQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<string?>> HandleAsync(
        Guid connectionId, K8sResourceKind kind, string? ns, string name, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.resource.yaml",
            secret => operations.GetYamlAsync(secret, kind, ns, name, ct), ct);
}
