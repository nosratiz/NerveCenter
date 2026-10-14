using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record SetStringCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Value, TimeSpan? Ttl = null);

public sealed class SetStringCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SetStringCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(SetStringCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.string.set", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating,
            cmd.Ttl is { } ttl ? string.Create(CultureInfo.InvariantCulture, $"ttl {ttl}") : null,
            secret => operations.SetStringAsync(secret, cmd.Database, cmd.Key, cmd.Value, cmd.Ttl, ct), ct);
}
