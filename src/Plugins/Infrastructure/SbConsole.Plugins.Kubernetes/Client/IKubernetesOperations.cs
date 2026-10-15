using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Client;

public interface IKubernetesOperations
{
    // Connection & access (Task 4)
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<K8sNamespace>> ListNamespacesAsync(string secret, CancellationToken ct = default);
    Task<K8sAccessFacts> GetAccessAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);

    // Overview (Task 8)
    Task<K8sOverview> GetOverviewAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);

    // Workloads (Task 10)
    Task<IReadOnlyList<K8sWorkload>> ListWorkloadsAsync(string secret, K8sWorkloadKind kind, IReadOnlyList<string> namespaces, string? labelSelector, CancellationToken ct = default);
    Task<IReadOnlyDictionary<K8sWorkloadKind, K8sKindCount>> CountWorkloadsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);
    Task<K8sScaleInfo> GetScaleInfoAsync(string secret, K8sWorkloadKind kind, string ns, string name, CancellationToken ct = default);
    Task<int> ScaleAsync(string secret, K8sWorkloadKind kind, string ns, string name, int replicas, CancellationToken ct = default); // returns previous replicas
    Task RestartAsync(string secret, K8sWorkloadKind kind, string ns, string name, DateTimeOffset at, CancellationToken ct = default);
    Task DeleteWorkloadAsync(string secret, K8sWorkloadKind kind, string ns, string name, CancellationToken ct = default);
    Task<string?> GetYamlAsync(string secret, K8sResourceKind kind, string? ns, string name, CancellationToken ct = default); // null = not found; managedFields stripped

    // Deployment detail (Task 12)
    Task<K8sDeploymentDetail?> GetDeploymentDetailAsync(string secret, string ns, string name, CancellationToken ct = default);
    Task<long> RollbackDeploymentAsync(string secret, string ns, string name, long toRevision, CancellationToken ct = default); // returns from-revision

    // Pods (Tasks 13–14)
    Task<K8sPodDetail?> GetPodDetailAsync(string secret, string ns, string name, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ReadLogsAsync(string secret, string ns, string pod, K8sLogRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> FollowLogsAsync(string secret, string ns, string pod, K8sLogRequest request, CancellationToken ct = default);
    Task<byte[]> DownloadAllLogsAsync(string secret, string ns, string pod, CancellationToken ct = default);
    Task DeletePodAsync(string secret, string ns, string name, int? gracePeriodSeconds, CancellationToken ct = default);

    // Nodes (Tasks 15–16)
    Task<IReadOnlyList<K8sNodeInfo>> ListNodesAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<K8sPodSummary>> ListPodsOnNodeAsync(string secret, string node, CancellationToken ct = default);
    Task SetUnschedulableAsync(string secret, string node, bool unschedulable, CancellationToken ct = default);
    Task<K8sDrainInputs> GetDrainInputsAsync(string secret, string node, CancellationToken ct = default);
    Task<K8sEvictionOutcome> EvictPodAsync(string secret, string ns, string name, int gracePeriodSeconds, CancellationToken ct = default);

    // Config (Task 17)
    Task<K8sConfigData> ListConfigMapsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);
    Task UpdateConfigMapAsync(string secret, string ns, string name, IReadOnlyDictionary<string, string> data, string resourceVersion, CancellationToken ct = default);
    Task<IReadOnlyList<SecretSummary>> ListSecretsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);

    // Events (Task 18)
    Task<IReadOnlyList<K8sEvent>> ListEventsAsync(string secret, IReadOnlyList<string> namespaces, TimeSpan window, CancellationToken ct = default);

    // Architecture
    Task<K8sArchitectureInputs> GetArchitectureInputsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default);
}
