using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Overview;

/// <summary>
/// JSON in the plugin store under "integrity:{connectionId}". Static over IPluginStore (not a DI
/// service) because SqlitePlugin.GetDashboardProblemsAsync is handed the store as a parameter.
/// </summary>
public static class IntegrityResultStore
{
    public static string Key(Guid connectionId) => $"integrity:{connectionId}";

    public static async Task<IntegrityResult?> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key(connectionId), ct);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IntegrityResult>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task WriteAsync(IPluginStore store, Guid connectionId, IntegrityResult result, CancellationToken ct = default) =>
        store.SetAsync(Key(connectionId), JsonSerializer.Serialize(result), ct);
}
