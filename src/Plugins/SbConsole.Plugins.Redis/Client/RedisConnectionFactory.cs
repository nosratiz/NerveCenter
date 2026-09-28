using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

internal static class RedisConnectionFactory
{
    public static async Task<IConnectionMultiplexer> ConnectAsync(string secret, CancellationToken ct)
    {
        var settings = RedisConnectionSettings.Parse(secret);
        if (settings.Topology == RedisTopology.Cluster)
        {
            throw new NotSupportedException("Cluster support ships in a later update.");
        }

        var options = settings.ToConfigurationOptions();
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 5_000;

        // Setting ServiceName puts StackExchange.Redis into Sentinel mode automatically: it
        // treats the configured endpoints as sentinels, discovers the current primary for that
        // service, and hands back a connection to the primary itself — no separate
        // SentinelConnectAsync/GetSentinelMasterConnectionAsync dance needed.
        return await ConnectionMultiplexer.ConnectAsync(options).WaitAsync(ct);
    }
}
