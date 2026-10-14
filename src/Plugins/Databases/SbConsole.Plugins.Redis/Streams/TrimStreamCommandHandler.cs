using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Streams;

public sealed record TrimStreamCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, long MaxLen);

/// <summary>XTRIM -- Destructive: drops entries permanently, same reasoning as the Keys browser's list-trim (design spec §6a).</summary>
public sealed class TrimStreamCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<TrimStreamCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(TrimStreamCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.stream.trim", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Destructive,
            string.Create(CultureInfo.InvariantCulture, $"maxlen {cmd.MaxLen}"),
            secret => operations.TrimStreamAsync(secret, cmd.Database, cmd.Key, cmd.MaxLen, ct), ct);
}
