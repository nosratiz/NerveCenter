using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Queues;

public sealed record CreateQueueCommand(Guid ConnectionId, string ConnectionName, string QueueName, int MaxDeliveryCount);

public sealed class CreateQueueCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(CreateQueueCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        try
        {
            await operations.CreateQueueAsync(secret, new CreateQueueRequest(cmd.QueueName, cmd.MaxDeliveryCount), ct);
        }
        catch (Exception ex)
        {
            await audit.RecordAsync("queue.create", target, ActionRisk.Mutating, succeeded: false, detail: ex.Message, ct: ct);
            return PluginResult.Fail(ex.Message);
        }

        await audit.RecordAsync("queue.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
