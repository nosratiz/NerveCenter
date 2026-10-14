using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    private const string PodTemplateHashLabel = "pod-template-hash";
    private const string ServiceNameLabel = "kubernetes.io/service-name";
    private const int MaxDeploymentEvents = 20;

    public async Task<K8sDeploymentDetail?> GetDeploymentDetailAsync(string secret, string ns, string name, CancellationToken ct = default)
    {
        var client = Client(secret);
        V1Deployment d;
        try
        {
            d = await client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 404)
        {
            return null;
        }

        var currentRevision = K8sMappers.Revision(d.Metadata);
        var templateLabels = d.Spec?.Template?.Metadata?.Labels ?? new Dictionary<string, string>();
        var selector = Selector(d.Spec?.Selector?.MatchLabels);

        var replicaSetsTask = OwnedReplicaSetsAsync(client, d, ct);
        var podsTask = client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: selector.Length == 0 ? null : selector, cancellationToken: ct);
        var servicesTask = MatchingServicesAsync(client, ns, templateLabels, ct);
        var hpaTask = FindHpaAsync(client, K8sWorkloadKind.Deployment, ns, name, ct);
        var eventsTask = NamespaceEventsAsync(client, ns, ct);
        await Task.WhenAll(replicaSetsTask, podsTask, servicesTask, hpaTask, eventsTask);

        var replicaSets = replicaSetsTask.Result
            .Select(rs => K8sMappers.ReplicaSet(rs, currentRevision))
            .OrderByDescending(r => r.Revision)
            .ToList();
        var rsNames = replicaSets.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var pods = podsTask.Result.Items
            .Select(K8sMappers.Pod)
            .Where(p => p.ReplicaSet is not null && rsNames.Contains(p.ReplicaSet))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var related = new HashSet<string>(rsNames, StringComparer.Ordinal) { name };
        related.UnionWith(pods.Select(p => p.Name));
        var events = eventsTask.Result
            .Where(e => e.Regarding.Kind is "Deployment" or "ReplicaSet" or "Pod" && related.Contains(e.Regarding.Name))
            .OrderByDescending(e => e.LastSeen)
            .Take(MaxDeploymentEvents)
            .ToList();

        var rolling = d.Spec?.Strategy?.RollingUpdate;
        var strategy = d.Spec?.Strategy?.Type ?? "RollingUpdate";
        return new K8sDeploymentDetail(
            K8sMappers.Deployment(d),
            strategy,
            strategy == "RollingUpdate" ? rolling?.MaxSurge?.Value ?? "25%" : null,
            strategy == "RollingUpdate" ? rolling?.MaxUnavailable?.Value ?? "25%" : null,
            selector,
            d.Spec?.ProgressDeadlineSeconds,
            d.Spec?.RevisionHistoryLimit ?? 10,
            K8sMappers.TemplateResources(d.Spec?.Template),
            currentRevision,
            replicaSets,
            pods,
            servicesTask.Result,
            events,
            hpaTask.Result is not null);
    }

    public async Task<long> RollbackDeploymentAsync(string secret, string ns, string name, long toRevision, CancellationToken ct = default)
    {
        var client = Client(secret);
        var d = await client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct);
        var target = (await OwnedReplicaSetsAsync(client, d, ct))
            .FirstOrDefault(rs => K8sMappers.Revision(rs.Metadata) == toRevision && rs.Spec?.Template is not null)
            ?? throw new InvalidOperationException($"Revision {toRevision} no longer exists.");

        // As kubectl rollout undo: the revision's pod template minus the hash label the ReplicaSet controller added.
        var template = JsonNode.Parse(KubernetesJson.Serialize(target.Spec.Template))!.AsObject();
        if (template["metadata"]?["labels"] is JsonObject labels)
        {
            labels.Remove(PodTemplateHashLabel);
        }

        var patch = new JsonArray(new JsonObject
        {
            ["op"] = "replace",
            ["path"] = "/spec/template",
            ["value"] = template,
        });
        await client.AppsV1.PatchNamespacedDeploymentAsync(
            new V1Patch(patch.ToJsonString(), V1Patch.PatchType.JsonPatch), name, ns, cancellationToken: ct);
        return K8sMappers.Revision(d.Metadata);
    }

    /// <summary>"k=v,k2=v2" from matchLabels, ordered by key; empty when there are none.</summary>
    private static string Selector(IDictionary<string, string>? matchLabels) =>
        matchLabels is null
            ? string.Empty
            : string.Join(",", matchLabels.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    private static async Task<IReadOnlyList<V1ReplicaSet>> OwnedReplicaSetsAsync(IKubernetes client, V1Deployment d, CancellationToken ct)
    {
        var uid = d.Metadata.Uid;
        var all = await client.AppsV1.ListNamespacedReplicaSetAsync(d.Metadata.NamespaceProperty, cancellationToken: ct);
        return all.Items
            .Where(rs => rs.Metadata.OwnerReferences?.Any(o => o.Uid == uid) == true)
            .ToList();
    }

    /// <summary>Services in <paramref name="ns"/> whose (non-empty) selector is a subset of the pod template's labels, with ready endpoints.</summary>
    private async Task<IReadOnlyList<K8sServiceRef>> MatchingServicesAsync(
        IKubernetes client, string ns, IDictionary<string, string> templateLabels, CancellationToken ct)
    {
        try
        {
            var services = (await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct)).Items
                .Where(s => s.Spec?.Selector is { Count: > 0 } sel
                    && sel.All(kv => templateLabels.TryGetValue(kv.Key, out var v) && v == kv.Value))
                .Select(s => s.Metadata.Name)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (services.Count == 0)
            {
                return [];
            }

            var slices = await client.DiscoveryV1.ListNamespacedEndpointSliceAsync(ns, labelSelector: ServiceNameLabel, cancellationToken: ct);
            var ready = slices.Items
                .Where(s => s.Metadata.Labels?.ContainsKey(ServiceNameLabel) == true)
                .GroupBy(s => s.Metadata.Labels[ServiceNameLabel], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.Endpoints?.Count(e => e.Conditions?.Ready == true) ?? 0), StringComparer.Ordinal);
            return services.Select(s => new K8sServiceRef(s, ready.GetValueOrDefault(s))).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing Services or EndpointSlices in {Namespace} is forbidden; the detail shows no Service", ns);
            return [];
        }
    }

    /// <summary>All events in one namespace (events.k8s.io, core/v1 when that is forbidden; none when both are).</summary>
    private async Task<IReadOnlyList<K8sEvent>> NamespaceEventsAsync(IKubernetes client, string ns, CancellationToken ct)
    {
        try
        {
            return (await client.EventsV1.ListNamespacedEventAsync(ns, cancellationToken: ct)).Items.Select(K8sMappers.Event).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing events.k8s.io events in {Namespace} is forbidden; reading core/v1 events instead", ns);
        }

        try
        {
            return (await client.CoreV1.ListNamespacedEventAsync(ns, cancellationToken: ct)).Items.Select(K8sMappers.Event).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing events in {Namespace} is forbidden; the detail shows none", ns);
            return [];
        }
    }
}
