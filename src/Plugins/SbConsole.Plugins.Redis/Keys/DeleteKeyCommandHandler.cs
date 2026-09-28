using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record DeleteKeyCommand(Guid ConnectionId, string ConnectionName, int Database, string Key);

public sealed class DeleteKeyCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteKeyCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(DeleteKeyCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.key.delete", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Destructive, null,
            secret => operations.DeleteKeyAsync(secret, cmd.Database, cmd.Key, ct), ct);
}
