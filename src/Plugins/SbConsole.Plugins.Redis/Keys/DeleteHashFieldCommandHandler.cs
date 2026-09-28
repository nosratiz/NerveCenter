using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record DeleteHashFieldCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Field);

public sealed class DeleteHashFieldCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteHashFieldCommandHandler> logger)
{
    // Mutating, not Destructive: removes one field, not the key itself (design spec §6).
    public Task<PluginResult> HandleAsync(DeleteHashFieldCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.hash.deletefield", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating, cmd.Field,
            secret => operations.DeleteHashFieldAsync(secret, cmd.Database, cmd.Key, cmd.Field, ct), ct);
}
