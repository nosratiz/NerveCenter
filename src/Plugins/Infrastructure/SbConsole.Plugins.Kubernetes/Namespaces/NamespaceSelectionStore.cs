using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Namespaces;

/// <summary>
/// The namespace selection each user last made on each cluster: a JSON string array under
/// "ns:{connectionId}:{user}". Empty means all namespaces; unreadable JSON is treated as empty.
/// </summary>
internal static class NamespaceSelectionStore
{
    public static string Key(Guid connectionId, string user) => $"ns:{connectionId}:{user}";

    public static async Task<IReadOnlyList<string>> GetAsync(IPluginStore store, Guid connectionId, string user, CancellationToken ct)
    {
        var json = await store.GetAsync(Key(connectionId, user), ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static Task SetAsync(IPluginStore store, Guid connectionId, string user, IReadOnlyList<string> namespaces, CancellationToken ct) =>
        store.SetAsync(Key(connectionId, user), JsonSerializer.Serialize(namespaces), ct);
}
