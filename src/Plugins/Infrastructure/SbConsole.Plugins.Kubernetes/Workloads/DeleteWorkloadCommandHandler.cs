using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Workloads;

/// <summary>
/// Deletes a workload (background propagation); audits <c>k8s.{kind}.delete</c> as Destructive. Confirmation is the
/// caller's job (the page asks through IConfirmationService with IsProd from the server-side connection).
/// </summary>
public sealed class DeleteWorkloadCommandHandler(
    IKubernetesOperations operations, IConnectionProvider connections, IAuditScope audit,
    ILogger<DeleteWorkloadCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(WorkloadCommand cmd, CancellationToken ct = default)
    {
        var t = cmd.Target;
        return CommandRunner.RunAsync(connections, audit, logger, t.ConnectionId,
            WorkloadAudit.Action(cmd.Kind, "delete"), t.AuditTarget, ActionRisk.Destructive,
            async secret =>
            {
                await operations.DeleteWorkloadAsync(secret, cmd.Kind, t.Namespace ?? string.Empty, t.Name, ct);
                return null;
            }, ct);
    }
}
