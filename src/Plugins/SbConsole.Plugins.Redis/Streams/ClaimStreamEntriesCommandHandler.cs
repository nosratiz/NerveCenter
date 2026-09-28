using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Streams;

public sealed record ClaimStreamEntriesCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Group, string Consumer, long MinIdleTimeMs, IReadOnlyList<string> EntryIds);

/// <summary>
/// XCLAIM -- Mutating: reassigns pending entries to a different consumer, doesn't delete data
/// (design spec §6a). Non-standard shape (like AddStreamEntryCommandHandler): the caller needs the
/// claimed entries back so the UI can show what actually moved (an id already claimed by someone
/// else, or no longer pending, silently isn't returned by XCLAIM).
/// </summary>
public sealed class ClaimStreamEntriesCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ClaimStreamEntriesCommandHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<StreamEntry>>> HandleAsync(ClaimStreamEntriesCommand cmd, CancellationToken ct = default)
    {
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<StreamEntry>>.Fail(HandlerRunner.ConnectionNotFound);
        }

        var target = HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key);
        var detail = string.Create(CultureInfo.InvariantCulture, $"group {cmd.Group} -> {cmd.Consumer}: {cmd.EntryIds.Count} id(s) requested");
        try
        {
            var claimed = await operations.ClaimStreamEntriesAsync(secret, cmd.Database, cmd.Key, cmd.Group, cmd.Consumer, cmd.MinIdleTimeMs, cmd.EntryIds, ct);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.stream.claim", target, ActionRisk.Mutating, succeeded: true,
                string.Create(CultureInfo.InvariantCulture, $"{detail}, {claimed.Count} claimed"));
            return PluginResult<IReadOnlyList<StreamEntry>>.Ok(claimed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "redis.stream.claim on {Target} failed.", target);
            var friendly = FriendlyRedisError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.stream.claim", target, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult<IReadOnlyList<StreamEntry>>.Fail(friendly);
        }
    }
}
