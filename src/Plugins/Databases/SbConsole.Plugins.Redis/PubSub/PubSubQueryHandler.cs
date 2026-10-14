using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.PubSub;

/// <summary>The Pub/Sub page's read-side handler: listing active channels. No database axis -- Pub/Sub is server-wide (design spec §14).</summary>
public sealed class PubSubQueryHandler(IRedisOperations operations, IConnectionProvider connections, ILogger<PubSubQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<IReadOnlyList<PubSubChannelSummary>>> GetChannelsAsync(Guid connectionId, string? pattern, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.pubsub.channels",
            secret => operations.GetPubSubChannelsAsync(secret, pattern, ct), ct);
}
