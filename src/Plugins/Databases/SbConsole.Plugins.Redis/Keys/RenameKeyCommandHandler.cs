using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record RenameKeyCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string NewKey);

public sealed class RenameKeyCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RenameKeyCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(RenameKeyCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.key.rename", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating, cmd.NewKey,
            secret => operations.RenameKeyAsync(secret, cmd.Database, cmd.Key, cmd.NewKey, ct), ct);
}
