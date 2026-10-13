using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// JSON array in the plugin store under "backups:{connectionId}" (design spec §11.7), newest first
/// and capped at MaxRecords. Static over IPluginStore like IntegrityResultStore, because
/// SqlitePlugin.GetDashboardProblemsAsync is handed the store as a parameter. Read-modify-write
/// without locking: the scheduler and a person on the page writing at the same instant can lose
/// one update -- acceptable for history (the file itself is on disk either way).
/// </summary>
public static class BackupHistoryStore
{
    public const int MaxRecords = 200;

    public static string Key(Guid connectionId) => $"backups:{connectionId}";

    public static async Task<IReadOnlyList<BackupRecord>> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct = default)
    {
        var json = await store.GetAsync(Key(connectionId), ct);
        if (json is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<BackupRecord>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Sorts newest first and keeps the newest MaxRecords.</summary>
    public static Task WriteAsync(IPluginStore store, Guid connectionId, IEnumerable<BackupRecord> records, CancellationToken ct = default) =>
        store.SetAsync(Key(connectionId), JsonSerializer.Serialize(records.OrderByDescending(r => r.TakenAt).Take(MaxRecords).ToList()), ct);

    public static async Task AddAsync(IPluginStore store, Guid connectionId, BackupRecord record, CancellationToken ct = default)
    {
        var records = await ReadAsync(store, connectionId, ct);
        await WriteAsync(store, connectionId, records.Where(r => r.Id != record.Id).Prepend(record), ct);
    }

    /// <summary>Replaces the record with the same Id; false (and nothing written) when it isn't in the history.</summary>
    public static async Task<bool> UpdateAsync(IPluginStore store, Guid connectionId, BackupRecord record, CancellationToken ct = default)
    {
        var records = await ReadAsync(store, connectionId, ct);
        if (records.All(r => r.Id != record.Id))
        {
            return false;
        }

        await WriteAsync(store, connectionId, records.Select(r => r.Id == record.Id ? record : r), ct);
        return true;
    }

    public static async Task RemoveAsync(IPluginStore store, Guid connectionId, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var remove = ids.ToHashSet();
        var records = await ReadAsync(store, connectionId, ct);
        await WriteAsync(store, connectionId, records.Where(r => !remove.Contains(r.Id)), ct);
    }
}
