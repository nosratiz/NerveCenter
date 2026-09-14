using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Queues;

public sealed class ListQueuesQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListQueuesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<QueueSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        try
        {
            // GetSecretAsync inside the try, not before it: ISecretProtector.Unprotect throws
            // CryptographicException/AuthenticationTagMismatchException for a corrupt or
            // wrong-key ciphertext (realistic after an SBC_DATA_KEY rotation or a database
            // restored against a different key), and a throw here would escape this handler
            // uncaught -- Queues.razor's LoadQueuesAsync only has a finally, no catch.
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<QueueSummary>>.Fail("Connection not found.");
            }

            var queues = await operations.ListQueuesAsync(secret, ct);
            return PluginResult<IReadOnlyList<QueueSummary>>.Ok(queues);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing queues for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<QueueSummary>>.Fail(ex);
        }
    }
}
