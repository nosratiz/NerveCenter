using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record SetHashFieldCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Field, string Value);

public sealed class SetHashFieldCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SetHashFieldCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(SetHashFieldCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.hash.setfield", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating, cmd.Field,
            secret => operations.SetHashFieldAsync(secret, cmd.Database, cmd.Key, cmd.Field, cmd.Value, ct), ct);
}
