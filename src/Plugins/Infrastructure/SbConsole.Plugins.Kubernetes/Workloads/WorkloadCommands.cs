using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Workloads;

public sealed record K8sTarget(Guid ConnectionId, string ConnectionName, string? Namespace, string Name)
{
    public string AuditTarget => Namespace is null ? $"{ConnectionName}/{Name}" : $"{ConnectionName}/{Namespace}/{Name}";
}
public sealed record ScaleCommand(K8sTarget Target, K8sWorkloadKind Kind, int Replicas);
public sealed record WorkloadCommand(K8sTarget Target, K8sWorkloadKind Kind);

internal static class WorkloadAudit
{
    /// <summary>"k8s.deployment.scale", "k8s.cronjob.delete", …</summary>
    public static string Action(K8sWorkloadKind kind, string verb) => $"k8s.{kind.ToString().ToLowerInvariant()}.{verb}";
}
