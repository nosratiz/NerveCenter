using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record PurgeDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string QueueName);

public sealed class PurgeDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit)
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
            await audit.RecordAsync("queue.purge", target, ActionRisk.Destructive, succeeded: false, detail: ex.Message, ct: ct);
            return PluginResult<int>.Fail(ex.Message);
        }

        await audit.RecordAsync("queue.purge", target, ActionRisk.Destructive, succeeded: true, detail: $"{purged} messages purged", ct: ct);
        return PluginResult<int>.Ok(purged);
    }
}
