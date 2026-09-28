using System.Globalization;

namespace SbConsole.Plugins.Redis.Client;

public static class RedisInfoParser
{
    public static ServerOverview Parse(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections)
    {
        var server = Section(sections, "Server");
        var clients = Section(sections, "Clients");
        var memory = Section(sections, "Memory");
        var stats = Section(sections, "Stats");
        var replication = Section(sections, "Replication");

        var maxMemory = GetLong(memory, "maxmemory");
        var hits = GetLong(stats, "keyspace_hits");
        var misses = GetLong(stats, "keyspace_misses");
        var totalLookups = hits + misses;

        return new ServerOverview(
            RedisVersion: GetString(server, "redis_version"),
            Mode: GetString(server, "redis_mode"),
            ConnectedReplicas: (int)GetLong(replication, "connected_slaves"),
            UsedMemoryBytes: GetLong(memory, "used_memory"),
            MaxMemoryBytes: maxMemory == 0 ? null : maxMemory,
            MaxMemoryPolicy: GetString(memory, "maxmemory_policy"),
            MemoryFragmentationRatio: GetDouble(memory, "mem_fragmentation_ratio"),
            HitRatioPercent: totalLookups == 0 ? 0 : 100.0 * hits / totalLookups,
            OpsPerSecond: GetLong(stats, "instantaneous_ops_per_sec"),
            EvictedKeysTotal: GetLong(stats, "evicted_keys"),
            ExpiredKeysTotal: GetLong(stats, "expired_keys"),
            ConnectedClients: GetLong(clients, "connected_clients"),
            BlockedClients: GetLong(clients, "blocked_clients"),
            Databases: ParseKeyspace(sections));
    }

    private static IReadOnlyList<KeyspaceDatabaseInfo> ParseKeyspace(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections)
    {
        if (!sections.TryGetValue("Keyspace", out var keyspace))
        {
            return [];
        }

        var result = new List<KeyspaceDatabaseInfo>();
        foreach (var (dbKey, value) in keyspace)
        {
            if (!dbKey.StartsWith("db", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(dbKey.AsSpan(2), out var index))
            {
                continue;
            }

            var fields = value.Split(',')
                .Select(f => f.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0], kv => kv[1]);

            var keys = fields.TryGetValue("keys", out var k) ? long.Parse(k, CultureInfo.InvariantCulture) : 0;
            var expires = fields.TryGetValue("expires", out var e) ? long.Parse(e, CultureInfo.InvariantCulture) : 0;
            result.Add(new KeyspaceDatabaseInfo(index, keys, expires));
        }

        return result.OrderBy(d => d.Index).ToList();
    }

    private static IReadOnlyDictionary<string, string> Section(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections, string name) =>
        sections.TryGetValue(name, out var section) ? section : new Dictionary<string, string>();

    private static string GetString(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) ? value : "";

    private static long GetLong(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) && long.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;

    private static double GetDouble(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) && double.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
}
