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

        if (settings.Topology == RedisTopology.Sentinel && string.IsNullOrEmpty(settings.ServiceName))
        {
            // Without a service name, StackExchange.Redis would connect in plain standalone mode
            // to the sentinel node itself, not the primary it fronts -- INFO would then report the
            // sentinel, not the real server, which is a confusing failure to debug from the UI.
            // Fail clearly and synchronously instead of connecting to the wrong node.
            throw new ArgumentException("Sentinel topology requires a service name.");
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
