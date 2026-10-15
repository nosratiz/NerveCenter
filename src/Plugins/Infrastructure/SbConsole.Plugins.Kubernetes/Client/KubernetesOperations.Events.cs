using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Kubernetes.Client;

public sealed partial class KubernetesOperations
{
    /// <summary>Every event (any type) in the selected namespaces (empty = all) last seen within <paramref name="window"/>, newest first.</summary>
    public async Task<IReadOnlyList<K8sEvent>> ListEventsAsync(string secret, IReadOnlyList<string> namespaces, TimeSpan window, CancellationToken ct = default)
    {
        var client = Client(secret);
        using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
        var events = await FetchEventsAsync(client, namespaces, null, gate, logger, ct);
        return InWindow(events, namespaces, _clock.GetUtcNow(), window);
    }

    /// <summary>
    /// Events via events.k8s.io: cluster-wide for "all namespaces", else one list per selected namespace through the caller's
    /// <paramref name="gate"/> (a cluster-wide list would pull every namespace's events only to drop them). The built-in
    /// "view" ClusterRole only grants core/v1 events, so the first 403 switches to the core API (same objects, older shape)
    /// in the same shape — cluster-wide (falling back per namespace when that is forbidden) or per selected namespace.
    /// <paramref name="type"/> ("Warning", "Normal") becomes a server-side field selector; null lists every type. Shared by
    /// the Overview and the Events page.
    /// </summary>
    internal static async Task<IReadOnlyList<K8sEvent>> FetchEventsAsync(IKubernetes client, IReadOnlyList<string> namespaces, string? type,
        SemaphoreSlim gate, ILogger logger, CancellationToken ct)
    {
        var fieldSelector = type is null ? null : $"type={type}";
        try
        {
            if (namespaces.Count == 0)
            {
                var events = await client.EventsV1.ListEventForAllNamespacesAsync(fieldSelector: fieldSelector, cancellationToken: ct);
                return events.Items.Select(K8sMappers.Event).ToList();
            }

            var perNamespace = await PerNamespaceAsync(namespaces,
                async ns => (await client.EventsV1.ListNamespacedEventAsync(ns, fieldSelector: fieldSelector, cancellationToken: ct)).Items, gate, ct);
            return perNamespace.Select(K8sMappers.Event).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing events.k8s.io events is forbidden; reading core/v1 events instead");
        }

        async Task<IList<Corev1Event>> CoreNamespaced(string ns) =>
            (await client.CoreV1.ListNamespacedEventAsync(ns, fieldSelector: fieldSelector, cancellationToken: ct)).Items;
        var core = namespaces.Count == 0
            ? await ListScopedAsync(namespaces,
                async () => (await client.CoreV1.ListEventForAllNamespacesAsync(fieldSelector: fieldSelector, cancellationToken: ct)).Items,
                CoreNamespaced, gate, ct)
            : await PerNamespaceAsync(namespaces, CoreNamespaced, gate, ct);
        return core.Select(K8sMappers.Event).ToList();
    }

    /// <summary>
    /// Events about specific objects in one namespace: one field-selected list per object (events.k8s.io
    /// <c>regarding.kind/name</c>; core/v1 <c>involvedObject.kind/name</c> after the first 403; none when both are
    /// forbidden), at most <see cref="MaxNamespaceFanOut"/> at a time. Detail pages poll this, so it only ever transfers
    /// the events they show rather than the whole namespace's.
    /// </summary>
    internal static async Task<IReadOnlyList<K8sEvent>> ObjectEventsAsync(IKubernetes client, string ns,
        IReadOnlyList<(string Kind, string Name)> objects, ILogger logger, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxNamespaceFanOut);
        try
        {
            var events = await FanOutAsync(objects, async o => (await client.EventsV1.ListNamespacedEventAsync(ns,
                fieldSelector: $"regarding.kind={o.Kind},regarding.name={o.Name}", cancellationToken: ct)).Items, gate, ct);
            return events.Select(K8sMappers.Event).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing events.k8s.io events in {Namespace} is forbidden; reading core/v1 events instead", ns);
        }

        try
        {
            var events = await FanOutAsync(objects, async o => (await client.CoreV1.ListNamespacedEventAsync(ns,
                fieldSelector: $"involvedObject.kind={o.Kind},involvedObject.name={o.Name}", cancellationToken: ct)).Items, gate, ct);
            return events.Select(K8sMappers.Event).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing events in {Namespace} is forbidden; the detail shows none", ns);
            return [];
        }
    }

    /// <summary>Events in the selected namespaces (empty = all) with <c>LastSeen ≥ now − window</c>, newest first.</summary>
    internal static IReadOnlyList<K8sEvent> InWindow(IEnumerable<K8sEvent> events, IReadOnlyList<string> namespaces, DateTimeOffset now, TimeSpan window)
    {
        var scope = namespaces.Count == 0 ? null : new HashSet<string>(namespaces, StringComparer.Ordinal);
        var since = now - window;
        return events
            .Where(e => scope is null || (e.Namespace is not null && scope.Contains(e.Namespace)))
            .Where(e => e.LastSeen >= since)
            .OrderByDescending(e => e.LastSeen)
            .ToList();
    }
}
