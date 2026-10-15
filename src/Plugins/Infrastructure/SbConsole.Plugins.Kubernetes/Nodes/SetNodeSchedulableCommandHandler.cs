using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Nodes;

/// <summary>Cordon (<see cref="Unschedulable"/> true) or uncordon a node. <see cref="K8sTarget.Namespace"/> is null: nodes are cluster-scoped.</summary>
public sealed record SetSchedulableCommand(K8sTarget Target, bool Unschedulable);

/// <summary>
/// Flips <c>spec.unschedulable</c>; audits <c>k8s.node.cordon</c> / <c>k8s.node.uncordon</c> as Mutating with no detail.
/// Confirmation is the caller's job (a plain two-button one).
/// </summary>
public sealed class SetNodeSchedulableCommandHandler(
    IKubernetesOperations operations, IConnectionProvider connections, IAuditScope audit,
    ILogger<SetNodeSchedulableCommandHandler> logger) : IScopedService
{
    public const string CordonAction = "k8s.node.cordon";
    public const string UncordonAction = "k8s.node.uncordon";

    public Task<PluginResult> HandleAsync(SetSchedulableCommand cmd, CancellationToken ct = default)
    {
        var t = cmd.Target;
        return CommandRunner.RunAsync(connections, audit, logger, t.ConnectionId,
            cmd.Unschedulable ? CordonAction : UncordonAction, t.AuditTarget, ActionRisk.Mutating,
            async secret =>
            {
                await operations.SetUnschedulableAsync(secret, t.Name, cmd.Unschedulable, ct);
                return null;
            }, ct);
    }
}
