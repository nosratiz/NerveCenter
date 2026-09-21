using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed class ListQueuesQueryHandler(ISqsOperations operations, IConnectionProvider connections, ILogger<ListQueuesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<QueueSummary>>> HandleAsync(Guid connectionId, string? namePrefix, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<QueueSummary>>.Fail("Connection not found.");
            }

            var queues = await operations.ListQueuesAsync(secret, namePrefix, ct);
            return PluginResult<IReadOnlyList<QueueSummary>>.Ok(queues);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing queues for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<QueueSummary>>.Fail(ex);
        }
    }
}
