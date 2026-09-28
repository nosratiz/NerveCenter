using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record PushListCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Value, bool AtHead);

public sealed class PushListCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PushListCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(PushListCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.list.push", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating, cmd.AtHead ? "head" : "tail",
            secret => operations.PushListAsync(secret, cmd.Database, cmd.Key, cmd.Value, cmd.AtHead, ct), ct);
}
