using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Events;

/// <summary>Events page query. <see cref="Type"/> is null (all), "Warning" or "Normal"; blank kind/search do not filter.</summary>
public sealed record EventQuery(IReadOnlyList<string> Namespaces, TimeSpan Window, string? Type, string? Kind, string? Search);

/// <summary><see cref="Events"/> are filtered, newest first; the counts and kinds cover the whole window, unfiltered.</summary>
public sealed record EventList(IReadOnlyList<K8sEvent> Events, int Total, int Warnings, int Normals, IReadOnlyList<string> Kinds);

/// <summary>Events in the selected namespaces (empty = all) over the window. Read-only: never audited.</summary>
public sealed class ListEventsQueryHandler(
    IKubernetesOperations operations, IConnectionProvider connections, ILogger<ListEventsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<EventList>> HandleAsync(Guid connectionId, EventQuery query, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "k8s.events.list",
            async secret => Filter(await operations.ListEventsAsync(secret, query.Namespaces, query.Window, ct), query), ct);

    internal static EventList Filter(IReadOnlyList<K8sEvent> window, EventQuery query)
    {
        var kind = string.IsNullOrWhiteSpace(query.Kind) ? null : query.Kind.Trim();
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var events = window
            .Where(e => query.Type is null || string.Equals(e.Type, query.Type, StringComparison.Ordinal))
            .Where(e => kind is null || string.Equals(e.Regarding.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .Where(e => search is null
                        || e.Reason.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.LastSeen)
            .ToList();
        var kinds = window.Select(e => e.Regarding.Kind)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new EventList(events, window.Count, window.Count(e => e.Type == "Warning"), window.Count(e => e.Type == "Normal"), kinds);
    }
}
