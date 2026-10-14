using k8s;
using k8s.Models;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    /// <summary>Pods that still hold their requests on a node: everything but finished ones.</summary>
    private const string ActivePodsSelector = "status.phase!=Succeeded,status.phase!=Failed";

    /// <summary>
    /// Every node with Σ requests of its active pods (one cluster-wide pod list), NotReady first, then by name. A 403 on
    /// either list propagates (the page says "Your account can't list nodes.").
    /// </summary>
    public async Task<IReadOnlyList<K8sNodeInfo>> ListNodesAsync(string secret, CancellationToken ct = default)
    {
        var client = Client(secret);
        var nodesTask = client.CoreV1.ListNodeAsync(cancellationToken: ct);
        var podsTask = client.CoreV1.ListPodForAllNamespacesAsync(fieldSelector: ActivePodsSelector, cancellationToken: ct);
        await Task.WhenAll(nodesTask, podsTask);
        return K8sMappers.InNodeDisplayOrder(NodesWithPods(nodesTask.Result.Items, podsTask.Result.Items.Select(K8sMappers.Pod)));
    }

    /// <summary>
    /// The node's active pods (finished ones excluded, so the list matches the node's pod count and the drain plan),
    /// by namespace then name.
    /// </summary>
    public async Task<IReadOnlyList<K8sPodSummary>> ListPodsOnNodeAsync(string secret, string node, CancellationToken ct = default)
    {
        var pods = await Client(secret).CoreV1.ListPodForAllNamespacesAsync(
            fieldSelector: $"spec.nodeName={node},{ActivePodsSelector}", cancellationToken: ct);
        return pods.Items.Select(K8sMappers.Pod)
            .OrderBy(p => p.Namespace, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Cordon / uncordon: a merge patch of the one field (what kubectl cordon sends).</summary>
    public async Task SetUnschedulableAsync(string secret, string node, bool unschedulable, CancellationToken ct = default)
    {
        var patch = new V1Patch($"{{\"spec\":{{\"unschedulable\":{(unschedulable ? "true" : "false")}}}}}", V1Patch.PatchType.MergePatch);
        await Client(secret).CoreV1.PatchNodeAsync(patch, node, cancellationToken: ct);
    }

    /// <summary>Maps each node with the pods scheduled on it (K8sMappers.Node sums the active ones). Shared by Overview and Nodes.</summary>
    private static List<K8sNodeInfo> NodesWithPods(IEnumerable<V1Node> nodes, IEnumerable<K8sPodSummary> pods)
    {
        var podsByNode = pods.Where(p => p.NodeName is not null).ToLookup(p => p.NodeName!, StringComparer.Ordinal);
        return nodes.Select(n => K8sMappers.Node(n, podsByNode[n.Metadata.Name])).ToList();
    }
}
