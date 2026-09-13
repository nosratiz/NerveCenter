using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record PurgeDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string QueueName);

public sealed class PurgeDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PurgeDeadLetterMessagesCommandHandler> logger)
{
    public async Task<PluginResult<int>> HandleAsync(PurgeDeadLetterMessagesCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<int>.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        int purged;
        try
        {
            purged = await operations.PurgeDeadLetterMessagesAsync(secret, cmd.QueueName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Purging the dead-letter queue of {Target} failed.", target);
            await audit.RecordAsync("queue.purge", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult<int>.Fail(ex);
        }

        await audit.RecordAsync("queue.purge", target, ActionRisk.Destructive, succeeded: true, detail: $"{purged} messages purged", ct: ct);
        return PluginResult<int>.Ok(purged);
    }
}
