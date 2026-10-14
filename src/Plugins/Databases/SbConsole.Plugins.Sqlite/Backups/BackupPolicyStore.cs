using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>JSON in the plugin store under "backup-policy:{connectionId}" (design spec §11.7). Missing, corrupt or invalid → null (= no schedule).</summary>
public static class BackupPolicyStore
{
    public static string Key(Guid connectionId) => $"backup-policy:{connectionId}";

    public static async Task<BackupPolicy?> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key(connectionId), ct);
        if (json is null)
        {
            return null;
        }

        try
        {
            var policy = JsonSerializer.Deserialize<BackupPolicy>(json);
            return policy?.Validate() is null ? policy : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Throws ArgumentException for an invalid policy -- the handler validates first.</summary>
    public static Task WriteAsync(IPluginStore store, Guid connectionId, BackupPolicy policy, CancellationToken ct = default)
    {
        if (policy.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(policy));
        }

        return store.SetAsync(Key(connectionId), JsonSerializer.Serialize(policy), ct);
    }
}
