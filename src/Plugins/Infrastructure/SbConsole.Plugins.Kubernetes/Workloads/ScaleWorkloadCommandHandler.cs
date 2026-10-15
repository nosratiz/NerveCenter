using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Workloads;

/// <summary>Scales a Deployment or StatefulSet; audits <c>k8s.{kind}.scale</c> with "{from} → {to} replicas".</summary>
public sealed class ScaleWorkloadCommandHandler(
    IKubernetesOperations operations, IConnectionProvider connections, IAuditScope audit,
    ILogger<ScaleWorkloadCommandHandler> logger) : IScopedService
{
    public const int MaxReplicas = 1000;

    public async Task<PluginResult> HandleAsync(ScaleCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Kind is not (K8sWorkloadKind.Deployment or K8sWorkloadKind.StatefulSet))
        {
            return PluginResult.Fail("Only Deployments and StatefulSets can be scaled.");
        }

        if (cmd.Replicas is < 0 or > MaxReplicas)
        {
            return PluginResult.Fail("Replicas must be between 0 and 1000.");
        }

        var t = cmd.Target;
        return await CommandRunner.RunAsync(connections, audit, logger, t.ConnectionId,
            WorkloadAudit.Action(cmd.Kind, "scale"), t.AuditTarget, ActionRisk.Mutating,
            async secret =>
            {
                var previous = await operations.ScaleAsync(secret, cmd.Kind, t.Namespace ?? string.Empty, t.Name, cmd.Replicas, ct);
                return $"{previous} → {cmd.Replicas} replicas";
            }, ct);
    }
}
