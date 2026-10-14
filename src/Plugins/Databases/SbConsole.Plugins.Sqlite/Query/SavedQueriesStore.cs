using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>
/// JSON array in the plugin store under "saved-queries", newest first. Static over IPluginStore
/// like IntegrityResultStore. Read-modify-write without locking: two people saving at the same
/// instant can lose one save, acceptable for a preference list.
/// </summary>
public static class SavedQueriesStore
{
    public const string Key = "saved-queries";

    public static async Task<IReadOnlyList<SavedQuery>> ReadAsync(IPluginStore store, CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key, ct);
        if (json is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SavedQuery>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static Task WriteAsync(IPluginStore store, IReadOnlyList<SavedQuery> queries, CancellationToken ct = default) =>
        store.SetAsync(Key, JsonSerializer.Serialize(queries), ct);
}
