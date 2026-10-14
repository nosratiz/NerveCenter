using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Workloads;

/// <summary>Confirmation requests shared by the Workloads list and the Deployment detail page.</summary>
public static class WorkloadConfirmations
{
    /// <summary>
    /// The Destructive delete confirmation. <paramref name="connection"/> must be the server-side <see cref="ConnectionInfo"/>
    /// (never a client-suppliable flag), because its <see cref="ConnectionInfo.IsProd"/> decides whether typing is required.
    /// </summary>
    public static ConfirmationRequest Delete(
        ConnectionInfo connection, K8sWorkloadKind kind, string ns, string name, int ready, int desired, IReadOnlyList<string> images) =>
        new($"Delete {kind}", name, connection.IsProd,
            Consequence: $"Deletes the {kind} and, in the background, everything it owns ({Owned(kind)}).",
            Details:
            [
                new ConfirmationDetail("Namespace", ns),
                new ConfirmationDetail("Ready", $"{ready}/{desired}"),
                new ConfirmationDetail("Images", K8sFormat.Images(images)),
            ],
            AuditAction: WorkloadAudit.Action(kind, "delete"),
            Destructive: true);

    private static string Owned(K8sWorkloadKind kind) => kind switch
    {
        K8sWorkloadKind.Deployment => "ReplicaSets and Pods",
        K8sWorkloadKind.CronJob => "Jobs and Pods",
        _ => "Pods",
    };
}
