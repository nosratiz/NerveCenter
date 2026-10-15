using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    /// <summary>
    /// Deployments, StatefulSets, DaemonSets and Services cluster-wide (so a dependency in another namespace resolves),
    /// falling back to the selected namespaces when that is forbidden; Ingresses and ConfigMaps for the selection only.
    /// Ingresses or ConfigMaps that can't be listed are treated as none. Env is mapped for workloads in the selection
    /// only: literal values and ConfigMap references are resolved, a Secret reference is never read (its value is null).
    /// </summary>
    public async Task<K8sArchitectureInputs> GetArchitectureInputsAsync(string secret, IReadOnlyList<string> namespaces, CancellationToken ct = default)
    {
        var client = Client(secret);
        var scope = namespaces.Count == 0 ? null : new HashSet<string>(namespaces, StringComparer.Ordinal);
        bool InScope(string? ns) => scope is null || (ns is not null && scope.Contains(ns));

        using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
        var deploymentsTask = ListScopedAsync(namespaces,
            async () => (await client.AppsV1.ListDeploymentForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: ct)).Items, gate, ct);
        var statefulSetsTask = ListScopedAsync(namespaces,
            async () => (await client.AppsV1.ListStatefulSetForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.AppsV1.ListNamespacedStatefulSetAsync(ns, cancellationToken: ct)).Items, gate, ct);
        var daemonSetsTask = ListScopedAsync(namespaces,
            async () => (await client.AppsV1.ListDaemonSetForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.AppsV1.ListNamespacedDaemonSetAsync(ns, cancellationToken: ct)).Items, gate, ct);
        var servicesTask = ListScopedAsync(namespaces,
            async () => (await client.CoreV1.ListServiceForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct)).Items, gate, ct);
        var ingressesTask = OptionalAsync("Ingresses", () => ScopedOnlyAsync(namespaces,
            async () => (await client.NetworkingV1.ListIngressForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.NetworkingV1.ListNamespacedIngressAsync(ns, cancellationToken: ct)).Items, gate, ct));
        var configMapsTask = OptionalAsync("ConfigMaps", () => ScopedOnlyAsync(namespaces,
            async () => (await client.CoreV1.ListConfigMapForAllNamespacesAsync(cancellationToken: ct)).Items,
            async ns => (await client.CoreV1.ListNamespacedConfigMapAsync(ns, cancellationToken: ct)).Items, gate, ct));
        await Task.WhenAll(deploymentsTask, statefulSetsTask, daemonSetsTask, servicesTask, ingressesTask, configMapsTask);

        var configMaps = configMapsTask.Result
            .GroupBy(m => (m.Metadata.NamespaceProperty ?? "", m.Metadata.Name))
            .ToDictionary(g => g.Key, g => (IDictionary<string, string>)(g.First().Data ?? new Dictionary<string, string>()));
        IReadOnlyList<K8sArchEnvVar> Env(V1ObjectMeta meta, V1PodTemplateSpec? template) =>
            InScope(meta.NamespaceProperty) ? ArchEnv(meta.NamespaceProperty ?? "", template, configMaps) : [];

        var workloads = new List<K8sArchWorkload>();
        workloads.AddRange(deploymentsTask.Result.Select(d =>
            ArchWorkload(K8sMappers.Deployment(d), d.Spec?.Template, Env(d.Metadata, d.Spec?.Template))));
        workloads.AddRange(statefulSetsTask.Result.Select(s =>
            ArchWorkload(K8sMappers.StatefulSet(s), s.Spec?.Template, Env(s.Metadata, s.Spec?.Template))));
        workloads.AddRange(daemonSetsTask.Result.Select(d =>
            ArchWorkload(K8sMappers.DaemonSet(d), d.Spec?.Template, Env(d.Metadata, d.Spec?.Template))));

        var services = servicesTask.Result.Select(s => new K8sArchService(
            s.Metadata.NamespaceProperty ?? "", s.Metadata.Name,
            new Dictionary<string, string>(s.Spec?.Selector ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            (s.Spec?.Ports ?? []).Select(p => p.Port).ToList(),
            s.Spec?.Type == "ExternalName" ? s.Spec.ExternalName : null)).ToList();

        var ingresses = ingressesTask.Result
            .Where(i => InScope(i.Metadata.NamespaceProperty))
            .Select(i => new K8sArchIngress(
                i.Metadata.NamespaceProperty ?? "", i.Metadata.Name,
                (i.Spec?.Rules ?? []).Select(r => r.Host).Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.Ordinal).ToList(),
                (i.Spec?.Rules ?? []).SelectMany(r => r.Http?.Paths ?? []).Select(p => p.Backend?.Service?.Name)
                    .Append(i.Spec?.DefaultBackend?.Service?.Name)
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct(StringComparer.Ordinal).ToList()))
            .ToList();

        return new K8sArchitectureInputs(workloads, services, ingresses);
    }

    private static K8sArchWorkload ArchWorkload(K8sWorkload w, V1PodTemplateSpec? template, IReadOnlyList<K8sArchEnvVar> env) =>
        new(w.Kind, w.Namespace, w.Name, w.Desired, w.Ready, w.IsUnhealthy, w.Labels,
            new Dictionary<string, string>(template?.Metadata?.Labels ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            w.Images, env);

    /// <summary>Main containers' env: literals, ConfigMap keys and envFrom ConfigMaps resolved; Secrets and field refs carry no value.</summary>
    private static IReadOnlyList<K8sArchEnvVar> ArchEnv(
        string ns, V1PodTemplateSpec? template, IReadOnlyDictionary<(string, string), IDictionary<string, string>> configMaps)
    {
        var rows = new List<K8sArchEnvVar>();
        foreach (var c in template?.Spec?.Containers ?? [])
        {
            foreach (var from in c.EnvFrom ?? [])
            {
                if (from.ConfigMapRef?.Name is { } cm && configMaps.TryGetValue((ns, cm), out var data))
                {
                    rows.AddRange(data.Select(kv => new K8sArchEnvVar((from.Prefix ?? "") + kv.Key, kv.Value)));
                }
            }

            foreach (var e in c.Env ?? [])
            {
                var value = e.ValueFrom switch
                {
                    null => e.Value,
                    { ConfigMapKeyRef: { } key } when configMaps.TryGetValue((ns, key.Name), out var data) => data.TryGetValue(key.Key, out var v) ? v : null,
                    _ => null,
                };
                rows.Add(new K8sArchEnvVar(e.Name, value));
            }
        }

        return rows;
    }

    /// <summary>Cluster-wide when nothing is selected, otherwise one list per selected namespace (never more than asked for).</summary>
    private static async Task<IReadOnlyList<T>> ScopedOnlyAsync<T>(
        IReadOnlyList<string> namespaces, Func<Task<IList<T>>> all, Func<string, Task<IList<T>>> one, SemaphoreSlim gate, CancellationToken ct) =>
        namespaces.Count == 0 ? (await all()).ToList() : await PerNamespaceAsync(namespaces, one, gate, ct);

    private async Task<IReadOnlyList<T>> OptionalAsync<T>(string what, Func<Task<IReadOnlyList<T>>> list)
    {
        try
        {
            return await list();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode is 403 or 404)
        {
            logger.LogDebug("Listing {What} for the architecture map is not allowed; treating it as none", what);
            return [];
        }
    }
}
