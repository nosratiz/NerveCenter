using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

/// <summary>DryRunKeyCount, when known, is folded into the success detail -- the design spec §5/§6 requires the confirmation to reflect the real dry-run count, not a client guess.</summary>
public sealed record DeleteByPatternCommand(Guid ConnectionId, string ConnectionName, int Database, string Pattern, long? DryRunKeyCount = null);

/// <summary>
/// Non-standard shape (like RabbitMQ's Publish/Republish): the caller needs the deleted count back,
/// not just success/failure, so this rolls its own ResolveSecretAsync/AuditQuietlyAsync flow instead
/// of HandlerRunner.CommandAsync (whose Func&lt;string, Task&gt; run delegate has no return value).
/// </summary>
public sealed class DeleteByPatternCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteByPatternCommandHandler> logger)
{
    public async Task<PluginResult<long>> HandleAsync(DeleteByPatternCommand cmd, CancellationToken ct = default)
    {
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<long>.Fail(HandlerRunner.ConnectionNotFound);
        }

        var target = HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Pattern);
        try
        {
            var deleted = await operations.DeleteByPatternAsync(secret, cmd.Database, cmd.Pattern, ct);
            var detail = cmd.DryRunKeyCount is { } dryRun
                ? string.Create(CultureInfo.InvariantCulture, $"{dryRun:N0} keys (dry run) · {deleted:N0} deleted")
                : string.Create(CultureInfo.InvariantCulture, $"{deleted:N0} deleted");
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.pattern.delete", target, ActionRisk.Destructive, succeeded: true, detail);
            return PluginResult<long>.Ok(deleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "redis.pattern.delete on {Target} failed.", target);
            var friendly = FriendlyRedisError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.pattern.delete", target, ActionRisk.Destructive, succeeded: false, friendly);
            return PluginResult<long>.Fail(friendly);
        }
    }
}
