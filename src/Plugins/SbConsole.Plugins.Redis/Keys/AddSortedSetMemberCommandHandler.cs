using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record AddSortedSetMemberCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Member, double Score);

public sealed class AddSortedSetMemberCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<AddSortedSetMemberCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(AddSortedSetMemberCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.zset.add", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating,
            string.Create(CultureInfo.InvariantCulture, $"{cmd.Member} score {cmd.Score}"),
            secret => operations.AddSortedSetMemberAsync(secret, cmd.Database, cmd.Key, cmd.Member, cmd.Score, ct), ct);
}
