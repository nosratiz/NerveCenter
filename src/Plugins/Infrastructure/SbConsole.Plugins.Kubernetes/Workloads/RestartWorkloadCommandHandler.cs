using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Workloads;

/// <summary>Rolling restart (kubectl rollout restart): stamps the pod template; audits <c>k8s.{kind}.restart</c>.</summary>
public sealed class RestartWorkloadCommandHandler(
    IKubernetesOperations operations, IConnectionProvider connections, IAuditScope audit, TimeProvider clock,
    ILogger<RestartWorkloadCommandHandler> logger) : IScopedService
{
    public async Task<PluginResult> HandleAsync(WorkloadCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Kind is not (K8sWorkloadKind.Deployment or K8sWorkloadKind.StatefulSet or K8sWorkloadKind.DaemonSet))
        {
            return PluginResult.Fail("Only Deployments, StatefulSets and DaemonSets can be restarted.");
        }

        var t = cmd.Target;
        var at = clock.GetUtcNow();
        return await CommandRunner.RunAsync(connections, audit, logger, t.ConnectionId,
            WorkloadAudit.Action(cmd.Kind, "restart"), t.AuditTarget, ActionRisk.Mutating,
            async secret =>
            {
                await operations.RestartAsync(secret, cmd.Kind, t.Namespace ?? string.Empty, t.Name, at, ct);
                return $"restartedAt {at.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture)}";
            }, ct);
    }
}
