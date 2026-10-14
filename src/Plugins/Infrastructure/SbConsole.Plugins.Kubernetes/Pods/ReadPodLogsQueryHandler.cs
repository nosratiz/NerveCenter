using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Pods;

/// <summary>One container's log (current or previous instance) as lines, for the Pod page's log viewer.</summary>
public sealed class ReadPodLogsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<ReadPodLogsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<string>>> HandleAsync(
        Guid connectionId, string ns, string pod, K8sLogRequest request, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.pod.logs",
            secret => operations.ReadLogsAsync(secret, ns, pod, request, ct), ct);
}
