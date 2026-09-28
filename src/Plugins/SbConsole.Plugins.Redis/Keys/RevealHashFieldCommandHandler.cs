using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record RevealHashFieldCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Field);

/// <summary>
/// HashEditor's "reveal" action on a masked field: read-only (the value is already in the page,
/// fetched by the scan), but the reveal itself is audited as Safe risk since the field is sensitive
/// (design spec §7a). No IRedisOperations/IConnectionProvider call -- there is nothing to resolve a
/// secret for.
/// </summary>
public sealed class RevealHashFieldCommandHandler(IAuditScope audit, ILogger<RevealHashFieldCommandHandler> logger)
{
    public Task RecordAsync(RevealHashFieldCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.field.reveal",
            HandlerRunner.Target(cmd.ConnectionName, cmd.Database, $"{cmd.Key}.{cmd.Field}"), ActionRisk.Safe, succeeded: true, null);
}
