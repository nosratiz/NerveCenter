using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record RemoveSetMemberCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Member);

public sealed class RemoveSetMemberCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RemoveSetMemberCommandHandler> logger) : IScopedService
{
    // Mutating, not Destructive: removes one member, not the key itself (design spec §6).
    public Task<PluginResult> HandleAsync(RemoveSetMemberCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.set.remove", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating, cmd.Member,
            secret => operations.RemoveSetMemberAsync(secret, cmd.Database, cmd.Key, cmd.Member, ct), ct);
}
