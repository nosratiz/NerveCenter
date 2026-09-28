using SbConsole.Sdk;
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

public sealed class RedisOperations : IRedisOperations
{
    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        try
        {
            await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
            var db = muxer.GetDatabase();
            var ping = await db.PingAsync();

            var server = muxer.GetServer(muxer.GetEndPoints()[0]);
            var sections = await GetInfoSectionsAsync(server, ct);
            var version = sections.TryGetValue("Server", out var serverSection)
                && serverSection.TryGetValue("redis_version", out var v) ? v : "unknown";

            return new ConnectionTestResult(
                Success: true,
                ErrorMessage: null,
                Identity: $"Redis {version}",
                Checks: [new ConnectionCheck("PING", ConnectionCheckStatus.Passed, $"{ping.TotalMilliseconds:N1} ms")]);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyRedisError.From(ex), null, null);
        }
    }

    public async Task<ServerOverview> GetOverviewAsync(string secret, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var server = muxer.GetServer(muxer.GetEndPoints()[0]);
        var sections = await GetInfoSectionsAsync(server, ct);
        return RedisInfoParser.Parse(sections);
    }

    public async Task<IReadOnlyList<MemoryByPrefixEntry>> GetMemoryByPrefixAsync(
        string secret, int database, char delimiter, int sampleSize, CancellationToken ct = default)
    {
        await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
        var server = muxer.GetServer(muxer.GetEndPoints()[0]);
        var db = muxer.GetDatabase(database);

        var samples = new List<SampledKey>();
        await foreach (var key in server.KeysAsync(database: database, pattern: "*", pageSize: 1000))
        {
            if (samples.Count >= sampleSize)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            var sizeResult = await db.ExecuteAsync("MEMORY", "USAGE", (string)key!);
            var size = sizeResult.IsNull ? 0L : (long)sizeResult;
            var ttl = await db.KeyTimeToLiveAsync(key);
            samples.Add(new SampledKey(key!, size, ttl.HasValue));
        }

        return RedisMemoryByPrefixSampler.Summarize(samples, delimiter, topN: 4);
    }

    internal static async Task<Dictionary<string, IReadOnlyDictionary<string, string>>> GetInfoSectionsAsync(
        IServer server, CancellationToken ct)
    {
        var groups = await server.InfoAsync().WaitAsync(ct);
        return groups.ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, string>)g.ToDictionary(
                kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }
}
