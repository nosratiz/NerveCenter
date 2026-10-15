using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Architecture;

/// <summary>
/// Ingress: entry points. Workload: the services the selection runs. Store: a workload whose image is a database,
/// cache or broker. Service: a Service no workload backs. External: a host outside the cluster.
/// </summary>
public enum ArchNodeKind { Ingress, Workload, Store, Service, External }

public enum ArchHealth { Ok, Degraded, Failing, Idle, Unknown }

/// <summary>Drawn thin (HTTP/gRPC), dashed (queues) or thick (stores).</summary>
public enum ArchEdgeKind { Http, Queue, Store }

/// <summary>
/// One box on the map. Ids: <c>w:{ns}/{kind}/{name}</c>, <c>i:{ns}/{name}</c>, <c>s:{ns}/{name}</c>, <c>x:{host}</c>.
/// <see cref="InScope"/> is false for a dependency that lives outside the selected namespaces.
/// </summary>
public sealed record ArchNode(
    string Id, ArchNodeKind Kind, string Name, string? Namespace, string Subtitle, ArchHealth Health,
    K8sWorkloadKind? WorkloadKind, string? StoreType, string? Team, string? Version, bool InScope);

/// <summary>
/// <paramref name="Source"/> says where the edge came from ("env REDIS_URL", "Ingress shop", "manual"), so a wrong
/// detection can be dismissed. Never carries an env value: values can hold credentials.
/// </summary>
public sealed record ArchEdge(string Id, string From, string To, ArchEdgeKind Kind, string Label, string Source, bool Manual)
{
    public static string MakeId(string from, string to) => $"{from}->{to}";
}

/// <summary><see cref="Dismissed"/> counts the detected edges hidden by the overlay.</summary>
public sealed record ArchitectureGraph(IReadOnlyList<ArchNode> Nodes, IReadOnlyList<ArchEdge> Edges, int Dismissed)
{
    public static readonly ArchitectureGraph Empty = new([], [], 0);
}

/// <summary>A hand-added dependency. <see cref="To"/> is a node id; <c>x:{host}</c> adds an external host.</summary>
public sealed record ManualEdge(string From, string To, ArchEdgeKind Kind, string? Label);

/// <summary>Console data layered over detection: hand-added edges and the ids of detected edges someone dismissed.</summary>
public sealed record ArchOverlay(IReadOnlyList<ManualEdge> Manual, IReadOnlyList<string> Dismissed)
{
    public static readonly ArchOverlay Empty = new([], []);
}
