using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed class GetQueueDetailQueryHandler(ISqsOperations operations, IConnectionProvider connections, ILogger<GetQueueDetailQueryHandler> logger)
{
    public async Task<PluginResult<QueueDetails>> HandleAsync(Guid connectionId, string queueUrl, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<QueueDetails>.Fail("Connection not found.");
            }

            var detail = await operations.GetQueueDetailAsync(secret, queueUrl, ct);
            return PluginResult<QueueDetails>.Ok(detail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading queue detail for {QueueUrl} failed.", queueUrl);
            return PluginResult<QueueDetails>.Fail(ex);
        }
    }
}
