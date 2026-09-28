using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Streams;

public sealed record DeleteStreamEntriesCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, IReadOnlyList<string> EntryIds);

/// <summary>XDEL -- Destructive: permanently removes entries from the stream (design spec §6a).</summary>
public sealed class DeleteStreamEntriesCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteStreamEntriesCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(DeleteStreamEntriesCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.stream.delete", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Destructive,
            string.Create(CultureInfo.InvariantCulture, $"{cmd.EntryIds.Count} id(s)"),
            secret => operations.DeleteStreamEntriesAsync(secret, cmd.Database, cmd.Key, cmd.EntryIds, ct), ct);
}
