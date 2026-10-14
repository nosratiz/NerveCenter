using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Overview;

public sealed class GetMemoryByPrefixQueryHandler(
    IRedisOperations operations, IConnectionProvider connections, ILogger<GetMemoryByPrefixQueryHandler> logger) : IScopedService
{
    private const char Delimiter = ':';
    private const int SampleSize = 5000;

    public Task<PluginResult<IReadOnlyList<MemoryByPrefixEntry>>> HandleAsync(
        Guid connectionId, int database, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.overview.memorybyprefix",
            secret => operations.GetMemoryByPrefixAsync(secret, database, Delimiter, SampleSize, ct), ct);
}
