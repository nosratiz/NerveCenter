using System.Text.Json;

namespace SbConsole.Sdk;

/// <summary>
/// Appends one point to a resource's metric history (a JSON array of MetricSnapshotPoint, oldest
/// first) under a plugin's IPluginStore, pruning anything older than the retention window. Shared
/// by the host's passive background collector (MetricsCollectorService, ticking every 60s so
/// history keeps growing even while nobody is looking) and a plugin page's own reload path (so the
/// trend line responds immediately to the action that just changed the real count -- sending,
/// consuming, deleting -- rather than waiting for the next passive tick).
/// </summary>
public static class MetricHistoryStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public static async Task AppendAsync(IPluginStore store, Guid connectionId, string resourceName, MetricSnapshotPoint point, CancellationToken ct = default)
    {
        var key = MetricHistoryKey.For(connectionId, resourceName);
        var existingJson = await store.GetAsync(key, ct);
        var points = existingJson is null
            ? []
            : JsonSerializer.Deserialize<List<MetricSnapshotPoint>>(existingJson) ?? [];

        points.Add(point);
        var cutoff = point.At - Retention;
        points.RemoveAll(p => p.At < cutoff);

        await store.SetAsync(key, JsonSerializer.Serialize(points), ct);
    }
}
