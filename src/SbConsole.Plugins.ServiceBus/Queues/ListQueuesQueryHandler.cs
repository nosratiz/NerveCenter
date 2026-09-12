using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Queues;

public sealed class ListQueuesQueryHandler(IServiceBusOperations operations, IConnectionProvider connections)
{
    public async Task<PluginResult<IReadOnlyList<QueueSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<QueueSummary>>.Fail("Connection not found.");
        }

        var queues = await operations.ListQueuesAsync(secret, ct);
        return PluginResult<IReadOnlyList<QueueSummary>>.Ok(queues);
    }
}
