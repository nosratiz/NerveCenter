using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Workloads;

public sealed record K8sTarget(Guid ConnectionId, string ConnectionName, string? Namespace, string Name)
{
    public string AuditTarget => Namespace is null ? $"{ConnectionName}/{Name}" : $"{ConnectionName}/{Namespace}/{Name}";
}
public sealed record ScaleCommand(K8sTarget Target, K8sWorkloadKind Kind, int Replicas);
public sealed record WorkloadCommand(K8sTarget Target, K8sWorkloadKind Kind);
