using k8s;
using k8s.Autorest;
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
    /// Events via events.k8s.io, cluster-wide. The built-in "view" ClusterRole only grants core/v1 events, so the first 403
    /// switches to the core API (same objects, older shape) — cluster-wide, then per selected namespace through the caller's
    /// <paramref name="gate"/> — rather than also trying events.k8s.io per namespace. <paramref name="type"/> ("Warning",
    /// "Normal") becomes a server-side field selector; null lists every type. Shared by the Overview and the Events page.
    /// </summary>
    internal static async Task<IReadOnlyList<K8sEvent>> FetchEventsAsync(IKubernetes client, IReadOnlyList<string> namespaces, string? type,
        SemaphoreSlim gate, ILogger logger, CancellationToken ct)
    {
        var fieldSelector = type is null ? null : $"type={type}";
        try
        {
            var events = await client.EventsV1.ListEventForAllNamespacesAsync(fieldSelector: fieldSelector, cancellationToken: ct);
            return events.Items.Select(K8sMappers.Event).ToList();
        }
        catch (HttpOperationException ex) when ((int?)ex.Response?.StatusCode == 403)
        {
            logger.LogDebug("Listing events.k8s.io events is forbidden; reading core/v1 events instead");
        }

        var core = await ListScopedAsync(namespaces,
            async () => (await client.CoreV1.ListEventForAllNamespacesAsync(fieldSelector: fieldSelector, cancellationToken: ct)).Items,
            async ns => (await client.CoreV1.ListNamespacedEventAsync(ns, fieldSelector: fieldSelector, cancellationToken: ct)).Items, gate, ct);
        return core.Select(K8sMappers.Event).ToList();
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
