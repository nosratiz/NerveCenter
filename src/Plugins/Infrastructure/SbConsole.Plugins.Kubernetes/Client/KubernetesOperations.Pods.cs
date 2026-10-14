using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    private const int MaxPodEvents = 50;

    /// <summary>
    /// The pod plus its owner chain, node health, resolved env (ConfigMaps read; Secrets never), events and YAML. Only the
    /// pod read itself can fail the detail (404 → null); a forbidden or missing owner, node, ConfigMap or event list just
    /// leaves that part out.
    /// </summary>
    public async Task<K8sPodDetail?> GetPodDetailAsync(string secret, string ns, string name, CancellationToken ct = default)
    {
        var client = Client(secret);
        V1Pod pod;
        try
        {
            pod = await client.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 404)
        {
            return null;
        }

        var ownerTask = OwnerMetaAsync(client, ns, K8sMappers.ControllerOf(pod.Metadata), ct);
        var nodeTask = NodeHealthAsync(client, pod.Spec?.NodeName, ct);
        var mapsTask = ConfigMapsAsync(client, ns, K8sMappers.ReferencedConfigMaps(pod), ct);
        var eventsTask = ObjectEventsAsync(client, ns, [("Pod", name)], logger, ct);
        await Task.WhenAll(ownerTask, nodeTask, mapsTask, eventsTask);

        var events = eventsTask.Result
            .OrderByDescending(e => e.LastSeen)
            .Take(MaxPodEvents)
            .ToList();

        return new K8sPodDetail(
            K8sMappers.Pod(pod),
            pod.Status?.PodIP,
            pod.Status?.HostIP,
            K8sMappers.QosClass(pod),
            pod.Spec?.ServiceAccountName,
            K8sMappers.VolumeCount(pod),
            K8sMappers.SecretMountCount(pod),
            K8sMappers.OwnerChain(pod, ownerTask.Result),
            nodeTask.Result,
            K8sMappers.Containers(pod),
            K8sMappers.Env(pod, mapsTask.Result),
            events,
            YamlDescribe.Serialize(pod));
    }

    public async Task DeletePodAsync(string secret, string ns, string name, int? gracePeriodSeconds, CancellationToken ct = default) =>
        await Client(secret).CoreV1.DeleteNamespacedPodAsync(name, ns, gracePeriodSeconds: gracePeriodSeconds, cancellationToken: ct);

    /// <summary>The metadata of the pod's controller when it is a kind whose own owner is worth showing; null on 403/404.</summary>
    private async Task<V1ObjectMeta?> OwnerMetaAsync(IKubernetes client, string ns, V1OwnerReference? owner, CancellationToken ct)
    {
        if (owner is null)
        {
            return null;
        }

        try
        {
            return owner.Kind switch
            {
                "ReplicaSet" => (await client.AppsV1.ReadNamespacedReplicaSetAsync(owner.Name, ns, cancellationToken: ct)).Metadata,
                "StatefulSet" => (await client.AppsV1.ReadNamespacedStatefulSetAsync(owner.Name, ns, cancellationToken: ct)).Metadata,
                "DaemonSet" => (await client.AppsV1.ReadNamespacedDaemonSetAsync(owner.Name, ns, cancellationToken: ct)).Metadata,
                "Job" => (await client.BatchV1.ReadNamespacedJobAsync(owner.Name, ns, cancellationToken: ct)).Metadata,
                _ => null,
            };
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode is 403 or 404)
        {
            logger.LogDebug("Reading {Kind} {Namespace}/{Name} failed ({Status}); the owner chain stops there",
                owner.Kind, ns, owner.Name, (int?)ex.Response?.StatusCode);
            return null;
        }
    }

    /// <summary>The pod's node (null when unscheduled, forbidden or gone); its memory fraction is null when the node's pods can't be listed.</summary>
    private async Task<K8sNodeHealth?> NodeHealthAsync(IKubernetes client, string? nodeName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nodeName))
        {
            return null;
        }

        V1Node node;
        try
        {
            node = await client.CoreV1.ReadNodeAsync(nodeName, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode is 403 or 404)
        {
            logger.LogDebug("Reading node {Node} failed ({Status}); the pod detail shows no node health", nodeName, (int?)ex.Response?.StatusCode);
            return null;
        }

        try
        {
            var pods = await client.CoreV1.ListPodForAllNamespacesAsync(fieldSelector: $"spec.nodeName={nodeName}", cancellationToken: ct);
            return K8sMappers.NodeHealth(K8sMappers.Node(node, pods.Items.Select(K8sMappers.Pod)), podsListed: true);
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing the pods on node {Node} is forbidden; its memory request fraction is unknown", nodeName);
            return K8sMappers.NodeHealth(K8sMappers.Node(node, []), podsListed: false);
        }
    }

    /// <summary>The named ConfigMaps that could be read (403/404 ones are left out, so their env rows show as Unresolved).</summary>
    private async Task<IReadOnlyDictionary<string, V1ConfigMap>> ConfigMapsAsync(
        IKubernetes client, string ns, IReadOnlyList<string> names, CancellationToken ct)
    {
        var maps = await Task.WhenAll(names.Select(async n =>
        {
            try
            {
                return await client.CoreV1.ReadNamespacedConfigMapAsync(n, ns, cancellationToken: ct);
            }
            catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode is 403 or 404)
            {
                logger.LogDebug("Reading ConfigMap {Namespace}/{Name} failed ({Status}); its env rows are unresolved", ns, n, (int?)ex.Response?.StatusCode);
                return null;
            }
        }));
        return maps.OfType<V1ConfigMap>().ToDictionary(m => m.Metadata.Name, StringComparer.Ordinal);
    }
}
