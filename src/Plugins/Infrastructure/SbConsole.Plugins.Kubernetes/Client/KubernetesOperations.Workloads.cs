using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    private const string RestartedAtAnnotation = "kubectl.kubernetes.io/restartedAt";

    public async Task<IReadOnlyList<K8sWorkload>> ListWorkloadsAsync(
        string secret, K8sWorkloadKind kind, IReadOnlyList<string> namespaces, string? labelSelector, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
        var workloads = await ListKindAsync(Client(secret), kind, namespaces, labelSelector, _clock.GetUtcNow(), gate, ct);
        var scope = namespaces.Count == 0 ? null : new HashSet<string>(namespaces, StringComparer.Ordinal);
        return workloads
            .Where(w => scope is null || scope.Contains(w.Namespace))
            .OrderBy(w => w.Namespace, StringComparer.Ordinal)
            .ThenBy(w => w.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<K8sWorkloadKind, K8sKindCount>> CountWorkloadsAsync(
        string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default)
    {
        var client = Client(secret);
        var now = _clock.GetUtcNow();
        var scope = namespaces.Count == 0 ? null : new HashSet<string>(namespaces, StringComparer.Ordinal);

        // One gate for the whole call: the five kinds share the cap of 8 namespace requests.
        using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
        var kinds = Enum.GetValues<K8sWorkloadKind>();
        var tasks = kinds.Select(k => ListKindAsync(client, k, namespaces, null, now, gate, ct)).ToArray();
        await Task.WhenAll(tasks);

        var counts = new Dictionary<K8sWorkloadKind, K8sKindCount>();
        for (var i = 0; i < kinds.Length; i++)
        {
            var inScope = tasks[i].Result.Where(w => scope is null || scope.Contains(w.Namespace)).ToList();
            counts[kinds[i]] = new K8sKindCount(inScope.Count, inScope.Any(w => w.IsUnhealthy));
        }

        return counts;
    }

    public async Task<K8sScaleInfo> GetScaleInfoAsync(string secret, K8sWorkloadKind kind, string ns, string name, CancellationToken ct = default)
    {
        var client = Client(secret);
        var hpaTask = FindHpaAsync(client, kind, ns, name, ct);
        int replicas, ready;
        V1PodTemplateSpec? template;
        switch (kind)
        {
            case K8sWorkloadKind.Deployment:
                var d = await client.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: ct);
                (replicas, ready, template) = (d.Spec?.Replicas ?? 1, d.Status?.ReadyReplicas ?? 0, d.Spec?.Template);
                break;
            case K8sWorkloadKind.StatefulSet:
                var s = await client.AppsV1.ReadNamespacedStatefulSetAsync(name, ns, cancellationToken: ct);
                (replicas, ready, template) = (s.Spec?.Replicas ?? 1, s.Status?.ReadyReplicas ?? 0, s.Spec?.Template);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Deployments and StatefulSets can be scaled.");
        }

        var hpa = await hpaTask;
        var (cpu, mem) = K8sMappers.TemplateRequests(template);
        return new K8sScaleInfo(replicas, ready, hpa is not null, hpa, cpu, mem);
    }

    public async Task<int> ScaleAsync(string secret, K8sWorkloadKind kind, string ns, string name, int replicas, CancellationToken ct = default)
    {
        var client = Client(secret);
        var patch = new V1Patch($"{{\"spec\":{{\"replicas\":{replicas}}}}}", V1Patch.PatchType.MergePatch);
        switch (kind)
        {
            case K8sWorkloadKind.Deployment:
                var d = await client.AppsV1.ReadNamespacedDeploymentScaleAsync(name, ns, cancellationToken: ct);
                await client.AppsV1.PatchNamespacedDeploymentScaleAsync(patch, name, ns, cancellationToken: ct);
                return d.Spec?.Replicas ?? 0;
            case K8sWorkloadKind.StatefulSet:
                var s = await client.AppsV1.ReadNamespacedStatefulSetScaleAsync(name, ns, cancellationToken: ct);
                await client.AppsV1.PatchNamespacedStatefulSetScaleAsync(patch, name, ns, cancellationToken: ct);
                return s.Spec?.Replicas ?? 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Deployments and StatefulSets can be scaled.");
        }
    }

    public async Task RestartAsync(string secret, K8sWorkloadKind kind, string ns, string name, DateTimeOffset at, CancellationToken ct = default)
    {
        var client = Client(secret);
        var stamp = at.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var patch = new V1Patch(
            $"{{\"spec\":{{\"template\":{{\"metadata\":{{\"annotations\":{{\"{RestartedAtAnnotation}\":\"{stamp}\"}}}}}}}}}}",
            V1Patch.PatchType.StrategicMergePatch);
        switch (kind)
        {
            case K8sWorkloadKind.Deployment:
                await client.AppsV1.PatchNamespacedDeploymentAsync(patch, name, ns, cancellationToken: ct);
                break;
            case K8sWorkloadKind.StatefulSet:
                await client.AppsV1.PatchNamespacedStatefulSetAsync(patch, name, ns, cancellationToken: ct);
                break;
            case K8sWorkloadKind.DaemonSet:
                await client.AppsV1.PatchNamespacedDaemonSetAsync(patch, name, ns, cancellationToken: ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Deployments, StatefulSets and DaemonSets can be restarted.");
        }
    }

    public async Task DeleteWorkloadAsync(string secret, K8sWorkloadKind kind, string ns, string name, CancellationToken ct = default)
    {
        var client = Client(secret);
        const string background = "Background";
        switch (kind)
        {
            case K8sWorkloadKind.Deployment:
                await client.AppsV1.DeleteNamespacedDeploymentAsync(name, ns, propagationPolicy: background, cancellationToken: ct);
                break;
            case K8sWorkloadKind.StatefulSet:
                await client.AppsV1.DeleteNamespacedStatefulSetAsync(name, ns, propagationPolicy: background, cancellationToken: ct);
                break;
            case K8sWorkloadKind.DaemonSet:
                await client.AppsV1.DeleteNamespacedDaemonSetAsync(name, ns, propagationPolicy: background, cancellationToken: ct);
                break;
            case K8sWorkloadKind.Job:
                await client.BatchV1.DeleteNamespacedJobAsync(name, ns, propagationPolicy: background, cancellationToken: ct);
                break;
            case K8sWorkloadKind.CronJob:
                await client.BatchV1.DeleteNamespacedCronJobAsync(name, ns, propagationPolicy: background, cancellationToken: ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    public async Task<string?> GetYamlAsync(string secret, K8sResourceKind kind, string? ns, string name, CancellationToken ct = default)
    {
        var client = Client(secret);
        try
        {
            object resource = kind switch
            {
                K8sResourceKind.Node => await client.CoreV1.ReadNodeAsync(name, cancellationToken: ct),
                K8sResourceKind.Deployment => await client.AppsV1.ReadNamespacedDeploymentAsync(name, Namespaced(kind, ns), cancellationToken: ct),
                K8sResourceKind.StatefulSet => await client.AppsV1.ReadNamespacedStatefulSetAsync(name, Namespaced(kind, ns), cancellationToken: ct),
                K8sResourceKind.DaemonSet => await client.AppsV1.ReadNamespacedDaemonSetAsync(name, Namespaced(kind, ns), cancellationToken: ct),
                K8sResourceKind.Job => await client.BatchV1.ReadNamespacedJobAsync(name, Namespaced(kind, ns), cancellationToken: ct),
                K8sResourceKind.CronJob => await client.BatchV1.ReadNamespacedCronJobAsync(name, Namespaced(kind, ns), cancellationToken: ct),
                K8sResourceKind.Pod => await client.CoreV1.ReadNamespacedPodAsync(name, Namespaced(kind, ns), cancellationToken: ct),
                K8sResourceKind.ConfigMap => await client.CoreV1.ReadNamespacedConfigMapAsync(name, Namespaced(kind, ns), cancellationToken: ct),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
            return YamlDescribe.Serialize(resource);
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 404)
        {
            return null;
        }
    }

    private static string Namespaced(K8sResourceKind kind, string? ns) =>
        string.IsNullOrEmpty(ns) ? throw new ArgumentException($"A {kind} needs a namespace.", nameof(ns)) : ns;

    /// <summary>One kind, cluster-wide or per selected namespace on 403 (<see cref="ListScopedAsync{T}"/>), mapped.</summary>
    private static async Task<IReadOnlyList<K8sWorkload>> ListKindAsync(
        IKubernetes client, K8sWorkloadKind kind, IReadOnlyList<string> namespaces, string? labelSelector,
        DateTimeOffset now, SemaphoreSlim gate, CancellationToken ct)
    {
        switch (kind)
        {
            case K8sWorkloadKind.Deployment:
                var deployments = await ListScopedAsync(namespaces,
                    async () => (await client.AppsV1.ListDeploymentForAllNamespacesAsync(labelSelector: labelSelector, cancellationToken: ct)).Items,
                    async ns => (await client.AppsV1.ListNamespacedDeploymentAsync(ns, labelSelector: labelSelector, cancellationToken: ct)).Items, gate, ct);
                return deployments.Select(K8sMappers.Deployment).ToList();
            case K8sWorkloadKind.StatefulSet:
                var statefulSets = await ListScopedAsync(namespaces,
                    async () => (await client.AppsV1.ListStatefulSetForAllNamespacesAsync(labelSelector: labelSelector, cancellationToken: ct)).Items,
                    async ns => (await client.AppsV1.ListNamespacedStatefulSetAsync(ns, labelSelector: labelSelector, cancellationToken: ct)).Items, gate, ct);
                return statefulSets.Select(K8sMappers.StatefulSet).ToList();
            case K8sWorkloadKind.DaemonSet:
                var daemonSets = await ListScopedAsync(namespaces,
                    async () => (await client.AppsV1.ListDaemonSetForAllNamespacesAsync(labelSelector: labelSelector, cancellationToken: ct)).Items,
                    async ns => (await client.AppsV1.ListNamespacedDaemonSetAsync(ns, labelSelector: labelSelector, cancellationToken: ct)).Items, gate, ct);
                return daemonSets.Select(K8sMappers.DaemonSet).ToList();
            case K8sWorkloadKind.Job:
                var jobs = await ListScopedAsync(namespaces,
                    async () => (await client.BatchV1.ListJobForAllNamespacesAsync(labelSelector: labelSelector, cancellationToken: ct)).Items,
                    async ns => (await client.BatchV1.ListNamespacedJobAsync(ns, labelSelector: labelSelector, cancellationToken: ct)).Items, gate, ct);
                return jobs.Select(j => K8sMappers.Job(j, now)).ToList();
            case K8sWorkloadKind.CronJob:
                var cronJobs = await ListScopedAsync(namespaces,
                    async () => (await client.BatchV1.ListCronJobForAllNamespacesAsync(labelSelector: labelSelector, cancellationToken: ct)).Items,
                    async ns => (await client.BatchV1.ListNamespacedCronJobAsync(ns, labelSelector: labelSelector, cancellationToken: ct)).Items, gate, ct);
                return cronJobs.Select(K8sMappers.CronJob).ToList();
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>The name of an autoscaling/v2 HPA in <paramref name="ns"/> targeting the workload, or null (also when HPAs can't be listed).</summary>
    private async Task<string?> FindHpaAsync(IKubernetes client, K8sWorkloadKind kind, string ns, string name, CancellationToken ct)
    {
        try
        {
            var hpas = await client.AutoscalingV2.ListNamespacedHorizontalPodAutoscalerAsync(ns, cancellationToken: ct);
            return hpas.Items
                .FirstOrDefault(h => h.Spec?.ScaleTargetRef is { } target
                    && string.Equals(target.Kind, kind.ToString(), StringComparison.Ordinal)
                    && string.Equals(target.Name, name, StringComparison.Ordinal))
                ?.Metadata.Name;
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing HorizontalPodAutoscalers in {Namespace} is forbidden; assuming no HPA", ns);
            return null;
        }
    }
}
