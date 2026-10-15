namespace SbConsole.Plugins.Kubernetes.Client;

public sealed record K8sWorkload(
    K8sWorkloadKind Kind, string Namespace, string Name, int Desired, int Ready, int UpToDate, int Available,
    IReadOnlyList<string> Images, DateTimeOffset CreatedAt, IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<K8sCondition> Conditions,
    // Job / CronJob only (null/0 otherwise):
    int? Completions, int Succeeded, int Failed, string? JobStatus /*Complete|Failed|Running|Suspended*/,
    string? FailureReason, TimeSpan? Duration, string? Schedule, bool Suspended, DateTimeOffset? LastSchedule, int Active)
{
    public bool IsUnhealthy => Kind switch
    {
        K8sWorkloadKind.Job => JobStatus == "Failed",
        K8sWorkloadKind.CronJob => false,
        _ => Desired > 0 && (Available < Desired
             || Conditions.Any(c => c.Type == "Progressing" && c.Status == "False")),
    };
}
public sealed record K8sKindCount(int Count, bool HasFailure);
public sealed record K8sScaleInfo(int Replicas, int Ready, bool HpaManaged, string? HpaName, long PerPodCpuMilli, long PerPodMemBytes);
public sealed record K8sReplicaSet(string Name, long Revision, IReadOnlyList<string> Images, int Desired, int Ready,
    DateTimeOffset CreatedAt, string? ChangeCause, bool IsCurrent, bool HasTemplate);
public sealed record K8sServiceRef(string Name, int ReadyEndpoints);
public sealed record K8sDeploymentDetail(
    K8sWorkload Deployment, string Strategy, string? MaxSurge, string? MaxUnavailable, string Selector,
    int? ProgressDeadlineSeconds, int RevisionHistoryLimit, K8sResources PodResources, long CurrentRevision,
    IReadOnlyList<K8sReplicaSet> ReplicaSets, IReadOnlyList<K8sPodSummary> Pods, IReadOnlyList<K8sServiceRef> Services,
    IReadOnlyList<K8sEvent> Events, bool HpaManaged);
