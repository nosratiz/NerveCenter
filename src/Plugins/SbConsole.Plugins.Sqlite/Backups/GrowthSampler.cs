using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>One growth sample (design spec §11.8), shared by the scheduler tick and the Overview load.</summary>
internal static class GrowthSampler
{
    /// <summary>Reads the file size and appends it unless the newest sample is under an hour old (the file isn't touched then). False when skipped.</summary>
    public static async Task<bool> SampleIfDueAsync(ISqliteOperations operations, IPluginStore store, Guid connectionId, string secret, DateTimeOffset now, CancellationToken ct)
    {
        if (!GrowthStore.IsDue(await GrowthStore.ReadAsync(store, connectionId, ct), now))
        {
            return false;
        }

        var bytes = await operations.GetFileBytesAsync(secret, ct);
        return bytes is { } value && await GrowthStore.AppendAsync(store, connectionId, new GrowthSample(now, value), ct);
    }
}
