using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.PubSub;

public sealed record PublishMessageCommand(Guid ConnectionId, string ConnectionName, string Channel, string Payload);

/// <summary>
/// PUBLISH -- Mutating (it injects data into other clients' streams, but doesn't destroy anything).
/// Non-standard shape (like AddStreamEntryCommandHandler): the caller wants the receiver count back
/// for the snackbar, not just success/failure.
/// </summary>
public sealed class PublishMessageCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PublishMessageCommandHandler> logger)
{
    public async Task<PluginResult<long>> HandleAsync(PublishMessageCommand cmd, CancellationToken ct = default)
    {
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<long>.Fail(HandlerRunner.ConnectionNotFound);
        }

        var target = $"{cmd.ConnectionName}/{cmd.Channel}";
        try
        {
            var receivers = await operations.PublishAsync(secret, cmd.Channel, cmd.Payload, ct);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.pubsub.publish", target, ActionRisk.Mutating, succeeded: true, $"{receivers} receiver(s)");
            return PluginResult<long>.Ok(receivers);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "redis.pubsub.publish to {Target} failed.", target);
            var friendly = FriendlyRedisError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.pubsub.publish", target, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult<long>.Fail(friendly);
        }
    }
}
