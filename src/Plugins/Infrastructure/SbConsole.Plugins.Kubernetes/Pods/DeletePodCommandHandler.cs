using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Pods;

public sealed record DeletePodCommand(K8sTarget Target);

/// <summary>
/// Deletes a pod (default grace period); audits <c>k8s.pod.delete</c> as Destructive with "owner {Kind}/{Name}" when the
/// pod has one. The pod is read first for that detail: gone → fails "It no longer exists — refresh." (audited as failed);
/// unreadable for any other reason → deleted anyway without the detail. Confirmation is the caller's job.
/// </summary>
public sealed class DeletePodCommandHandler(
    IKubernetesOperations operations, IConnectionProvider connections, IAuditScope audit,
    ILogger<DeletePodCommandHandler> logger) : IScopedService
{
    public const string AuditAction = "k8s.pod.delete";
    public const string Gone = "It no longer exists — refresh.";

    public Task<PluginResult> HandleAsync(DeletePodCommand cmd, CancellationToken ct = default)
    {
        var t = cmd.Target;
        var ns = t.Namespace ?? string.Empty;
        return CommandRunner.RunAsync(connections, audit, logger, t.ConnectionId, AuditAction, t.AuditTarget, ActionRisk.Destructive,
            async secret =>
            {
                var owner = await OwnerAsync(secret, ns, t.Name, ct);
                await operations.DeletePodAsync(secret, ns, t.Name, null, ct);
                return owner is null ? null : $"owner {owner.Kind}/{owner.Name}";
            }, ct);
    }

    /// <summary>The pod's top-level owner; throws <see cref="InvalidOperationException"/>(<see cref="Gone"/>) when the pod is gone.</summary>
    private async Task<K8sRef?> OwnerAsync(string secret, string ns, string name, CancellationToken ct)
    {
        K8sPodDetail? detail;
        try
        {
            detail = await operations.GetPodDetailAsync(secret, ns, name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reading pod {Namespace}/{Name} before deleting it failed; deleting without the owner detail", ns, name);
            return null;
        }

        return detail is null ? throw new InvalidOperationException(Gone) : detail.Pod.Owner;
    }
}
