using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Streams;

public sealed record AcknowledgeStreamEntriesCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Group, IReadOnlyList<string> EntryIds);

/// <summary>XACK -- Mutating: removes entries from the PEL, doesn't delete data (design spec §6a).</summary>
public sealed class AcknowledgeStreamEntriesCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<AcknowledgeStreamEntriesCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(AcknowledgeStreamEntriesCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.stream.ack", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Mutating,
            string.Create(CultureInfo.InvariantCulture, $"group {cmd.Group}: {cmd.EntryIds.Count} id(s)"),
            secret => operations.AcknowledgeStreamEntriesAsync(secret, cmd.Database, cmd.Key, cmd.Group, cmd.EntryIds, ct), ct);
}
