using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Queues;

public sealed record CreateQueueCommand(Guid ConnectionId, string ConnectionName, string QueueName, int MaxDeliveryCount);

public sealed class CreateQueueCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateQueueCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateQueueCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.CreateQueueAsync(secret, new CreateQueueRequest(cmd.QueueName, cmd.MaxDeliveryCount), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating queue {Target} failed.", target);
            await audit.RecordAsync("queue.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("queue.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
