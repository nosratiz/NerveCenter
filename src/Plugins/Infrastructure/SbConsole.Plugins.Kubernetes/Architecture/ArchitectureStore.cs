using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Architecture;

/// <summary>A node's description: plain text (Markdown source), numbered so a stale editor can't overwrite a newer one.</summary>
public sealed record ArchDescription(string Text, int Version, string Author, DateTimeOffset At);

public sealed record StoredManualEdge(string From, string To, ArchEdgeKind Kind, string? Label, string Author, DateTimeOffset At);

internal sealed record OverlayDoc(IReadOnlyList<StoredManualEdge> Manual, IReadOnlyList<string> Dismissed)
{
    public static readonly OverlayDoc Empty = new([], []);

    public ArchOverlay ToOverlay() => new(Manual.Select(m => new ManualEdge(m.From, m.To, m.Kind, m.Label)).ToList(), Dismissed);
}

/// <summary>
/// Architecture console data per connection, as JSON in the plugin store: "arch:overlay:{id}" (manual edges, dismissed
/// detected edges) and "arch:desc:{id}" (node id → description). Read-modify-write goes through one process-wide gate,
/// so two editors saving different nodes at once can't drop each other's change (the console is one process).
/// Unreadable JSON reads as empty.
/// </summary>
internal static class ArchitectureStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string OverlayKey(Guid connectionId) => $"arch:overlay:{connectionId}";

    public static string DescriptionsKey(Guid connectionId) => $"arch:desc:{connectionId}";

    public static async Task<OverlayDoc> ReadOverlayAsync(IPluginStore store, Guid connectionId, CancellationToken ct) =>
        Read(await store.GetAsync(OverlayKey(connectionId), ct), OverlayDoc.Empty);

    public static async Task<IReadOnlyDictionary<string, ArchDescription>> ReadDescriptionsAsync(IPluginStore store, Guid connectionId, CancellationToken ct) =>
        Read<Dictionary<string, ArchDescription>>(await store.GetAsync(DescriptionsKey(connectionId), ct), new(StringComparer.Ordinal));

    /// <summary>Applies <paramref name="change"/> under the gate; it returns the new document or throws an <see cref="ArchEditException"/>.</summary>
    public static Task<T> UpdateOverlayAsync<T>(IPluginStore store, Guid connectionId, Func<OverlayDoc, (OverlayDoc Doc, T Result)> change, CancellationToken ct) =>
        UpdateAsync(store, OverlayKey(connectionId), OverlayDoc.Empty, change, ct);

    public static Task<T> UpdateDescriptionsAsync<T>(
        IPluginStore store, Guid connectionId,
        Func<Dictionary<string, ArchDescription>, (Dictionary<string, ArchDescription> Doc, T Result)> change, CancellationToken ct) =>
        UpdateAsync(store, DescriptionsKey(connectionId), new Dictionary<string, ArchDescription>(StringComparer.Ordinal), change, ct);

    private static async Task<T> UpdateAsync<TDoc, T>(IPluginStore store, string key, TDoc empty, Func<TDoc, (TDoc Doc, T Result)> change, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var current = Read(await store.GetAsync(key, ct), empty);
            var (doc, result) = change(current);
            await store.SetAsync(key, JsonSerializer.Serialize(doc, Options), ct);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static T Read<T>(string? json, T empty)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return empty;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? empty;
        }
        catch (JsonException)
        {
            return empty;
        }
    }
}

/// <summary>A refused edit (conflict, duplicate, nothing changed): its message is shown as is and nothing is audited.</summary>
internal sealed class ArchEditException(string message) : Exception(message);
