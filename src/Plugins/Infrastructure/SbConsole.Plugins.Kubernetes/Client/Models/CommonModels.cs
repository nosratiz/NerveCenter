namespace SbConsole.Plugins.Kubernetes.Client;

public enum K8sWorkloadKind { Deployment, StatefulSet, DaemonSet, Job, CronJob }
public enum K8sResourceKind { Deployment, StatefulSet, DaemonSet, Job, CronJob, Pod, Node, ConfigMap }
public sealed record K8sRef(string Kind, string? Namespace, string Name);
public sealed record K8sCondition(string Type, string Status, string? Reason, string? Message, DateTimeOffset? LastTransition);
public sealed record K8sResources(long? CpuRequestMilli, long? MemRequestBytes, long? CpuLimitMilli, long? MemLimitBytes);
public sealed record K8sEvent(
    string? Namespace, string Type /*Warning|Normal*/, string Reason, string Message, K8sRef Regarding,
    int Count, DateTimeOffset LastSeen, string? Reporter);
public sealed record K8sNamespace(string Name, int PodCount);
