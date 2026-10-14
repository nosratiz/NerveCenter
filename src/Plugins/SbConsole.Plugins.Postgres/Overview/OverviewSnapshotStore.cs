using System.Text.Json;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Overview;

/// <summary>JSON in the plugin store under "overview:{connectionId}": last good snapshot plus the last failure.</summary>
internal static class OverviewSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Key(Guid connectionId) => $"overview:{connectionId}";

    public static async Task<OverviewState> ReadAsync(IPluginStore store, Guid connectionId, CancellationToken ct)
    {
        var json = await store.GetAsync(Key(connectionId), ct);
        if (json is null)
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<OverviewState>(json, Options) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static Task WriteGoodAsync(IPluginStore store, Guid connectionId, OverviewSnapshot snapshot, CancellationToken ct) =>
        WriteAsync(store, connectionId, new OverviewState(snapshot, null, null), ct);

    public static async Task WriteFailureAsync(IPluginStore store, Guid connectionId, DateTimeOffset at, string error, CancellationToken ct)
    {
        var current = await ReadAsync(store, connectionId, ct);
        await WriteAsync(store, connectionId, current with { LastFailureAt = at, LastError = error }, ct);
    }

    private static OverviewState Empty => new(null, null, null);

    private static Task WriteAsync(IPluginStore store, Guid connectionId, OverviewState state, CancellationToken ct) =>
        store.SetAsync(Key(connectionId), JsonSerializer.Serialize(state, Options), ct);
}
