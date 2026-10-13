using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// JSON array of samples in the plugin store under "growth:{connectionId}" (design spec §11.8),
/// oldest first: at most one per MinInterval, nothing older than Retention. Static over
/// IPluginStore like IntegrityResultStore.
/// </summary>
public static class GrowthStore
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public static string Key(Guid connectionId) => $"growth:{connectionId}";

    public static async Task<IReadOnlyList<GrowthSample>> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key(connectionId), ct);
        if (json is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<GrowthSample>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>True when the newest sample is missing or at least MinInterval older than <paramref name="now"/>.</summary>
    public static bool IsDue(IReadOnlyList<GrowthSample> samples, DateTimeOffset now) =>
        samples.Count == 0 || now - samples.Max(s => s.At) >= MinInterval;

    /// <summary>Appends unless the newest sample is under an hour old; prunes anything past Retention. False when skipped.</summary>
    public static async Task<bool> AppendAsync(IPluginStore store, Guid connectionId, GrowthSample sample, CancellationToken ct = default)
    {
        var samples = await ReadAsync(store, connectionId, ct);
        if (!IsDue(samples, sample.At))
        {
            return false;
        }

        var kept = samples.Append(sample).Where(s => sample.At - s.At <= Retention).OrderBy(s => s.At).ToList();
        await store.SetAsync(Key(connectionId), JsonSerializer.Serialize(kept), ct);
        return true;
    }
}
