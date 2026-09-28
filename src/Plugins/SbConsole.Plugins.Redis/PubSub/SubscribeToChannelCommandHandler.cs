using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.PubSub;

/// <summary>
/// Starts a live SUBSCRIBE/PSUBSCRIBE tail. Read-only (subscribing has no side effect on the data),
/// but -- like Keys/RevealHashFieldCommandHandler's field reveal -- it's audited as Safe risk since
/// starting to watch live traffic is a deliberate, worth-recording action, not a page-load query;
/// this is why it lives here as a "Command" handler despite not writing anything (same naming
/// precedent RevealHashFieldCommandHandler set). Only the start is audited, not every message or the
/// eventual unsubscribe, matching "audit what you did" rather than a per-message audit flood.
///
/// Rolls its own ResolveSecretAsync/AuditQuietlyAsync flow (like AddStreamEntryCommandHandler)
/// because the return value is the live IAsyncDisposable subscription handle, not a plain
/// success/failure HandlerRunner.CommandAsync can express.
/// </summary>
public sealed class SubscribeToChannelCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SubscribeToChannelCommandHandler> logger)
{
    public async Task<PluginResult<IAsyncDisposable>> HandleAsync(
        Guid connectionId, string connectionName, string channelOrPattern, bool isPattern, Func<PubSubMessage, Task> onMessage, CancellationToken ct = default)
    {
        var secret = await HandlerRunner.ResolveSecretAsync(connections, connectionId, ct);
        if (secret is null)
        {
            return PluginResult<IAsyncDisposable>.Fail(HandlerRunner.ConnectionNotFound);
        }

        // No database axis for Pub/Sub (design spec §14) -- the target string drops the "db{n}"
        // segment HandlerRunner.Target uses for every other Redis audit target.
        var target = $"{connectionName}/{channelOrPattern}";
        try
        {
            var subscription = await operations.SubscribeAsync(secret, channelOrPattern, isPattern, onMessage, ct);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.pubsub.subscribe", target, ActionRisk.Safe, succeeded: true, isPattern ? "pattern" : "channel");
            return PluginResult<IAsyncDisposable>.Ok(subscription);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "redis.pubsub.subscribe to {Target} failed.", target);
            var friendly = FriendlyRedisError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.pubsub.subscribe", target, ActionRisk.Safe, succeeded: false, friendly);
            return PluginResult<IAsyncDisposable>.Fail(friendly);
        }
    }
}
