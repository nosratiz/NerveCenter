using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Events;

public enum EventGroupBy { InvolvedObject, Reason, None }

/// <summary>
/// One group on the Events page (mockup 3i). <see cref="Object"/> is set when grouped by involved object, <see cref="Reason"/>
/// when grouped by reason; both are null for the single <see cref="EventGroupBy.None"/> group. <see cref="WarningCount"/> sums
/// the Warning events' own counts (count / series), so a collapsed group still ranks by how often it fired.
/// </summary>
public sealed record EventGroup(string Key, K8sRef? Object, string? Reason, IReadOnlyList<K8sEvent> Events, int WarningCount,
    DateTimeOffset Newest, IReadOnlyList<string> TopReasons);

public static class EventGrouping
{
    private const int MaxTopReasons = 2;

    /// <summary>
    /// Groups ordered by <see cref="EventGroup.Newest"/> desc (key as tie-break), events in a group newest first.
    /// <see cref="EventGroupBy.None"/> is one group with key "" holding every event; no events are no groups.
    /// </summary>
    public static IReadOnlyList<EventGroup> Group(IReadOnlyList<K8sEvent> events, EventGroupBy by)
    {
        if (events.Count == 0)
        {
            return [];
        }

        if (by == EventGroupBy.None)
        {
            return [Build("", null, null, events)];
        }

        return events
            .GroupBy(e => by == EventGroupBy.Reason ? e.Reason : ObjectKey(e.Regarding), StringComparer.Ordinal)
            .Select(g => by == EventGroupBy.Reason
                ? Build(g.Key, null, g.Key, g.ToList())
                : Build(g.Key, g.First().Regarding, null, g.ToList()))
            .OrderByDescending(g => g.Newest)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static string ObjectKey(K8sRef r) => $"{r.Kind}/{r.Namespace}/{r.Name}";

    private static EventGroup Build(string key, K8sRef? obj, string? reason, IReadOnlyList<K8sEvent> events)
    {
        var ordered = events.OrderByDescending(e => e.LastSeen).ToList();
        var topReasons = ordered
            .GroupBy(e => e.Reason, StringComparer.Ordinal)
            .Select(g => (Reason: g.Key, Count: g.Sum(e => e.Count), Newest: g.Max(e => e.LastSeen)))
            .OrderByDescending(r => r.Count)
            .ThenByDescending(r => r.Newest)
            .ThenBy(r => r.Reason, StringComparer.Ordinal)
            .Take(MaxTopReasons)
            .Select(r => r.Reason)
            .ToList();
        return new EventGroup(key, obj, reason, ordered, ordered.Where(e => e.Type == "Warning").Sum(e => e.Count),
            ordered[0].LastSeen, topReasons);
    }
}
