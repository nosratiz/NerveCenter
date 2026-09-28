using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Overview;

public sealed class GetOverviewQueryHandler(
    IRedisOperations operations, IConnectionProvider connections, ILogger<GetOverviewQueryHandler> logger)
{
    public Task<PluginResult<ServerOverview>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.overview.get",
            secret => operations.GetOverviewAsync(secret, ct), ct);
}
