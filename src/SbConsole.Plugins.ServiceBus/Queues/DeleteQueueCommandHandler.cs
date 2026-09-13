using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Queues;

public sealed record DeleteQueueCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string QueueName);

public sealed class DeleteQueueCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteQueueCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteQueueCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        try
        {
            await operations.DeleteQueueAsync(secret, cmd.QueueName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting queue {Target} failed.", target);
            await audit.RecordAsync("queue.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("queue.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
