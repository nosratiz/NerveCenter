using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record AddSetMemberCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Member);

public sealed class AddSetMemberCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<AddSetMemberCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(AddSetMemberCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.set.add", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating, cmd.Member,
            secret => operations.AddSetMemberAsync(secret, cmd.Database, cmd.Key, cmd.Member, ct), ct);
}
