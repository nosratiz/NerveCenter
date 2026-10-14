using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record FlushDatabaseCommand(Guid ConnectionId, string ConnectionName, int Database);

/// <summary>FLUSHDB -- Destructive: wipes every key in the selected database (design spec §12/§14, deferred from phase 1).</summary>
public sealed class FlushDatabaseCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<FlushDatabaseCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(FlushDatabaseCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.database.flush", $"{cmd.ConnectionName}/db{cmd.Database}", ActionRisk.Destructive, null,
            secret => operations.FlushDatabaseAsync(secret, cmd.Database, ct), ct);
}
