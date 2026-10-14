using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Deployments;

public sealed record RollbackCommand(K8sTarget Target, long ToRevision);

/// <summary>Rolls a Deployment back to a revision (kubectl rollout undo); audits <c>k8s.deployment.rollback</c> "rev {from} → rev {to}".</summary>
public sealed class RollbackDeploymentCommandHandler(
    IKubernetesOperations operations, IConnectionProvider connections, IAuditScope audit,
    ILogger<RollbackDeploymentCommandHandler> logger) : IScopedService
{
    public const string AuditAction = "k8s.deployment.rollback";

    public Task<PluginResult> HandleAsync(RollbackCommand cmd, CancellationToken ct = default)
    {
        var t = cmd.Target;
        return CommandRunner.RunAsync(connections, audit, logger, t.ConnectionId, AuditAction, t.AuditTarget, ActionRisk.Mutating,
            async secret =>
            {
                var from = await operations.RollbackDeploymentAsync(secret, t.Namespace ?? string.Empty, t.Name, cmd.ToRevision, ct);
                return $"rev {from} → rev {cmd.ToRevision}";
            }, ct);
    }
}
