using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record PurgeSubscriptionDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName);

public sealed class PurgeSubscriptionDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PurgeSubscriptionDeadLetterMessagesCommandHandler> logger)
{
    public async Task<PluginResult<int>> HandleAsync(PurgeSubscriptionDeadLetterMessagesCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}";
        int purged;
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult<int>.Fail("Connection not found.");
            }

            purged = await operations.PurgeSubscriptionDeadLetterMessagesAsync(secret, cmd.TopicName, cmd.SubscriptionName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Purging the dead-letter queue of {Target} failed.", target);
            await audit.RecordAsync("subscription.purge", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult<int>.Fail(ex);
        }

        await audit.RecordAsync("subscription.purge", target, ActionRisk.Destructive, succeeded: true, detail: $"{purged} messages purged", ct: ct);
        return PluginResult<int>.Ok(purged);
    }
}
