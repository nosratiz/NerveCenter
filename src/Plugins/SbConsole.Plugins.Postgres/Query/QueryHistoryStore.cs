using System.Collections.Concurrent;
using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>
/// JSON array in the plugin store under "history:{connectionId}", newest first. Appends drop entries older than
/// Retention and cap at MaxEntries. Read-modify-write, so appends are serialised per connection in-process
/// (two circuits can run queries at the same instant).
/// </summary>
internal static class QueryHistoryStore
{
    public const int MaxEntries = 500;
    public const int MaxSqlLength = 4000;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public static string Key(Guid connectionId) => $"history:{connectionId}";

    public static async Task<IReadOnlyList<QueryHistoryEntry>> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct)
    {
        var json = await store.GetAsync(Key(connectionId), ct);
        if (json is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<QueryHistoryEntry>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task AppendAsync(IPluginStore store, Guid connectionId, QueryHistoryEntry entry, DateTimeOffset now, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var stored = entry.Sql.Length > MaxSqlLength ? entry with { Sql = entry.Sql[..MaxSqlLength] } : entry;
            var cutoff = now - Retention;
            var existing = await ReadAsync(store, connectionId, ct);
            var kept = existing.Where(e => e.At >= cutoff).Prepend(stored).Take(MaxEntries).ToList();
            await store.SetAsync(Key(connectionId), JsonSerializer.Serialize(kept), ct);
        }
        finally
        {
            gate.Release();
        }
    }
}
