namespace SbConsole.Plugins.Kubernetes.Client;

public sealed record K8sPodSummary(
    string Namespace, string Name, string Phase, string Status /*display: Running|Pending|CrashLoopBackOff|Completed|Terminating|ImagePullBackOff|…*/,
    int Restarts, int ReadyContainers, int TotalContainers, string? NodeName, K8sRef? Owner /*top-level: Deployment via RS hash, else direct owner*/,
    string? ReplicaSet, string? LastTerminationReason, int? LastExitCode, DateTimeOffset? LastTerminatedAt,
    string? UnschedulableMessage, DateTimeOffset CreatedAt, long CpuRequestMilli, long MemRequestBytes,
    bool HasEmptyDir, IReadOnlyDictionary<string, string> Labels, bool IsDaemonSetPod, bool IsMirrorPod);
public sealed record K8sTermination(int ExitCode, string? Reason, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
public sealed record K8sContainer(
    string Name, bool IsInit, string Image, string State /*Running|Waiting|Terminated*/, string? StateReason,
    K8sTermination? CurrentTermination, K8sTermination? LastTermination, int Restarts, bool Ready,
    IReadOnlyList<string> Ports /*"8080/http"*/, K8sResources Resources);
public enum K8sEnvSource { Literal, ConfigMap, Secret, FieldRef, ResourceFieldRef, Unresolved }
public sealed record K8sEnvVar(string Container, string Name, string? Value /*always null for Secret*/, K8sEnvSource Source, string? SourceName);
public sealed record K8sOwnerLink(string Kind, string Name, long? Revision);
public sealed record K8sNodeHealth(string Name, string ReadyStatus, IReadOnlyList<string> Pressures, double? MemoryRequestFraction);
public sealed record K8sPodDetail(
    K8sPodSummary Pod, string? PodIp, string? HostIp, string QosClass, string? ServiceAccount,
    int VolumeCount, int SecretMountCount, IReadOnlyList<K8sOwnerLink> OwnerChain, K8sNodeHealth? Node,
    IReadOnlyList<K8sContainer> Containers, IReadOnlyList<K8sEnvVar> Env, IReadOnlyList<K8sEvent> Events,
    string DescribeYaml);
public sealed record K8sLogRequest(string Container, bool Previous, int TailLines, bool Timestamps);
