namespace SbConsole.Plugins.Kubernetes.Client;

/// <summary>An env var as the architecture detector sees it. <see cref="Value"/> is null when it comes from a Secret or can't be resolved.</summary>
public sealed record K8sArchEnvVar(string Name, string? Value);

/// <summary>A Deployment, StatefulSet or DaemonSet with what dependency detection needs: pod labels (for Service selectors), images and env.</summary>
public sealed record K8sArchWorkload(
    K8sWorkloadKind Kind, string Namespace, string Name, int Desired, int Ready, bool Unhealthy,
    IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> PodLabels,
    IReadOnlyList<string> Images, IReadOnlyList<K8sArchEnvVar> Env);

public sealed record K8sArchService(
    string Namespace, string Name, IReadOnlyDictionary<string, string> Selector, IReadOnlyList<int> Ports, string? ExternalName);

/// <summary><see cref="Services"/> are the backend Service names the rules route to (same namespace).</summary>
public sealed record K8sArchIngress(string Namespace, string Name, IReadOnlyList<string> Hosts, IReadOnlyList<string> Services);

/// <summary>
/// Raw inputs for the Architecture page. Workloads and Services are cluster-wide when the account may list them (so a
/// dependency in another namespace resolves), otherwise only the selected namespaces; Ingresses cover the selection.
/// </summary>
public sealed record K8sArchitectureInputs(
    IReadOnlyList<K8sArchWorkload> Workloads, IReadOnlyList<K8sArchService> Services, IReadOnlyList<K8sArchIngress> Ingresses);
