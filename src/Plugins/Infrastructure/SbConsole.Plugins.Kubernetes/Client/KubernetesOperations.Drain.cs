using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    /// <summary>
    /// What the drain planner needs: the node, its active pods, the PDBs in those pods' namespaces and the desired
    /// replicas of each Deployment / StatefulSet / bare ReplicaSet owner. The node and pod reads must succeed; a forbidden
    /// PDB list or owner read just leaves that part out (the Eviction API still enforces every PDB).
    /// </summary>
    public async Task<K8sDrainInputs> GetDrainInputsAsync(string secret, string node, CancellationToken ct = default)
    {
        var client = Client(secret);
        var nodeTask = client.CoreV1.ReadNodeAsync(node, cancellationToken: ct);
        var podsTask = ListPodsOnNodeAsync(secret, node, ct);
        await Task.WhenAll(nodeTask, podsTask);
        var pods = podsTask.Result;

        using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
        var namespaces = pods.Where(p => !p.IsMirrorPod && !p.IsDaemonSetPod)
            .Select(p => p.Namespace).Distinct(StringComparer.Ordinal).ToList();
        var owners = pods.Where(p => !p.IsMirrorPod && !p.IsDaemonSetPod && p.Owner is { Kind: "Deployment" or "StatefulSet" or "ReplicaSet" })
            .Select(p => p.Owner! with { Namespace = p.Namespace })
            .Distinct()
            .ToList();

        var pdbsTask = Task.WhenAll(namespaces.Select(ns => Gated(gate, () => PdbsAsync(client, ns, ct), ct)));
        var replicasTask = Task.WhenAll(owners.Select(o => Gated(gate, () => ReplicasAsync(client, o, ct), ct)));
        await Task.WhenAll(pdbsTask, replicasTask);

        var replicas = owners.Zip(replicasTask.Result)
            .Where(x => x.Second is not null)
            .ToDictionary(x => K8sDrainInputs.OwnerKey(x.First, x.First.Namespace!), x => x.Second!.Value, StringComparer.Ordinal);

        return new K8sDrainInputs(K8sMappers.Node(nodeTask.Result, pods), pods, pdbsTask.Result.SelectMany(p => p).ToList(), replicas);
    }

    /// <summary>POST pods/{name}/eviction (what kubectl drain sends). 429 = a PDB refuses it now; 404 = the pod is already gone.</summary>
    public async Task<K8sEvictionOutcome> EvictPodAsync(string secret, string ns, string name, int gracePeriodSeconds, CancellationToken ct = default)
    {
        var eviction = new V1Eviction
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
            DeleteOptions = new V1DeleteOptions { GracePeriodSeconds = gracePeriodSeconds },
        };
        try
        {
            await Client(secret).CoreV1.CreateNamespacedPodEvictionAsync(eviction, name, ns, cancellationToken: ct);
            return K8sEvictionOutcome.Evicted;
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 429)
        {
            return K8sEvictionOutcome.BlockedByPdb;
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 404)
        {
            return K8sEvictionOutcome.NotFound;
        }
    }

    private static async Task<T> Gated<T>(SemaphoreSlim gate, Func<Task<T>> run, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await run();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The namespace's PDBs that select something (a null selector selects no pods in policy/v1); none on 403.</summary>
    private async Task<IReadOnlyList<K8sPdb>> PdbsAsync(IKubernetes client, string ns, CancellationToken ct)
    {
        try
        {
            var list = await client.PolicyV1.ListNamespacedPodDisruptionBudgetAsync(ns, cancellationToken: ct);
            return list.Items.Where(p => p.Spec?.Selector is not null).Select(K8sMappers.Pdb).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing PodDisruptionBudgets in {Namespace} is forbidden; the drain preview can't show PDB blocks there", ns);
            return [];
        }
    }

    /// <summary>The owner's desired replicas (a pod's Deployment owner comes from its ReplicaSet's hash); null on 403/404.</summary>
    private async Task<int?> ReplicasAsync(IKubernetes client, K8sRef owner, CancellationToken ct)
    {
        var ns = owner.Namespace!;
        try
        {
            return owner.Kind switch
            {
                "Deployment" => (await client.AppsV1.ReadNamespacedDeploymentAsync(owner.Name, ns, cancellationToken: ct)).Spec?.Replicas ?? 1,
                "StatefulSet" => (await client.AppsV1.ReadNamespacedStatefulSetAsync(owner.Name, ns, cancellationToken: ct)).Spec?.Replicas ?? 1,
                "ReplicaSet" => (await client.AppsV1.ReadNamespacedReplicaSetAsync(owner.Name, ns, cancellationToken: ct)).Spec?.Replicas ?? 1,
                _ => null,
            };
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode is 403 or 404)
        {
            logger.LogDebug("Reading {Kind} {Namespace}/{Name} failed ({Status}); its drain note has no replica count",
                owner.Kind, ns, owner.Name, (int?)ex.Response?.StatusCode);
            return null;
        }
    }
}
