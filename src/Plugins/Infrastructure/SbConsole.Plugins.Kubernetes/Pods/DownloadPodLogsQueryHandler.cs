using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Pods;

/// <summary>"Download logs (all containers)": every container's current log as one UTF-8 text file.</summary>
public sealed class DownloadPodLogsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<DownloadPodLogsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<byte[]>> HandleAsync(Guid connectionId, string ns, string pod, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.pod.logs.download",
            secret => operations.DownloadAllLogsAsync(secret, ns, pod, ct), ct);
}
