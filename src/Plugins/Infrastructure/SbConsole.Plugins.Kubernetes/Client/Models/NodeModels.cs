namespace SbConsole.Plugins.Kubernetes.Client;

public sealed record K8sNodeInfo(
    string Name, string? Pool, string? InstanceType, string? InternalIp, string ReadyStatus /*True|False|Unknown*/,
    DateTimeOffset? ReadySince, IReadOnlyList<string> Pressures, IReadOnlyList<string> Roles, IReadOnlyList<string> Taints,
    string KubeletVersion, bool Unschedulable, long CpuAllocatableMilli, long MemAllocatableBytes,
    long CpuRequestedMilli, long MemRequestedBytes, int PodCount, DateTimeOffset CreatedAt, string? ProviderId,
    IReadOnlyDictionary<string, string> Labels);
public sealed record K8sPdb(string Namespace, string Name, IReadOnlyDictionary<string, string> Selector,
    int DisruptionsAllowed, string Rule /*"minAvailable 3" | "maxUnavailable 0"*/, int CurrentHealthy, int DesiredHealthy);
public sealed record K8sDrainInputs(K8sNodeInfo Node, IReadOnlyList<K8sPodSummary> Pods, IReadOnlyList<K8sPdb> Pdbs,
    IReadOnlyDictionary<string, int> OwnerReplicas /*key "Kind/ns/name"*/)
{
    /// <summary>The <see cref="OwnerReplicas"/> key: "Kind/ns/name".</summary>
    public static string OwnerKey(K8sRef owner, string podNamespace) => $"{owner.Kind}/{owner.Namespace ?? podNamespace}/{owner.Name}";
}
public enum K8sEvictionOutcome { Evicted, BlockedByPdb, NotFound }
