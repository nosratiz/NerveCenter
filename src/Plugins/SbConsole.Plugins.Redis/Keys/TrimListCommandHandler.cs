using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record TrimListCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, long Start, long Stop);

public sealed class TrimListCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<TrimListCommandHandler> logger)
{
    // Destructive: can drop list members permanently (design spec §6).
    public Task<PluginResult> HandleAsync(TrimListCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.list.trim", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Destructive,
            string.Create(CultureInfo.InvariantCulture, $"keep {cmd.Start}..{cmd.Stop}"),
            secret => operations.TrimListAsync(secret, cmd.Database, cmd.Key, cmd.Start, cmd.Stop, ct), ct);
}
