namespace SbConsole.Plugins.Kubernetes.Client;

public sealed record K8sClusterInfo(string ServerUrl, string Context, string ServerVersion, string Provider,
    string Identity, int NamespaceCount);
public sealed record K8sPodCounts(int Running, int Pending, int Failed, int Succeeded);
public sealed record K8sOverview(
    DateTimeOffset CapturedAt, K8sClusterInfo Cluster, IReadOnlyList<K8sNodeInfo> Nodes, K8sPodCounts Pods,
    IReadOnlyList<K8sPodSummary> ProblemPods /*CrashLoopBackOff, Pending-unschedulable, Failed*/,
    IReadOnlyList<K8sWorkload> ProblemWorkloads /*unhealthy Deployments + failed Jobs*/,
    IReadOnlyList<K8sEvent> RecentWarnings /*last 1 h, newest first, max 50*/, int WarningCount,
    long CpuLimitsMilli, long MemLimitsBytes, double? CpuUsageFraction, double? MemUsageFraction);
