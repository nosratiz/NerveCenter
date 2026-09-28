using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Overview;

public sealed class GetSlowLogQueryHandler(
    IRedisOperations operations, IConnectionProvider connections, ILogger<GetSlowLogQueryHandler> logger)
{
    private const int Count = 10;

    public Task<PluginResult<IReadOnlyList<SlowLogEntry>>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.overview.slowlog",
            secret => operations.GetSlowLogAsync(secret, Count, ct), ct);
}
