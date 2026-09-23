using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed class GetTopicDeliveryFailureCountQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<GetTopicDeliveryFailureCountQueryHandler> logger)
{
    public async Task<PluginResult<long>> HandleAsync(Guid connectionId, string topicName, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<long>.Fail("Connection not found.");
            }

            var count = await operations.GetDeliveryFailureCountAsync(secret, topicName, ct);
            return PluginResult<long>.Ok(count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Getting delivery failure count for topic {TopicName} failed.", topicName);
            return PluginResult<long>.Fail(ex);
        }
    }
}
