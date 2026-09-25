using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

/// <summary>
/// Read-only query (no audit row): recent SNS delivery-status log events for a topic, from
/// CloudWatch Logs. Backs TopicDetail's Delivery logs tab.
/// </summary>
public sealed class GetTopicDeliveryLogsQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<GetTopicDeliveryLogsQueryHandler> logger)
{
    public const int DefaultLimit = 200;

    public async Task<PluginResult<DeliveryLogsResult>> HandleAsync(Guid connectionId, string topicArn, TimeSpan window, int limit = DefaultLimit, CancellationToken ct = default)
    {
        if (window <= TimeSpan.Zero || limit <= 0)
        {
            return PluginResult<DeliveryLogsResult>.Fail("The time window and limit must be positive.");
        }

        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<DeliveryLogsResult>.Fail("Connection not found.");
            }

            var logs = await operations.GetDeliveryLogsAsync(secret, topicArn, window, limit, ct);
            return PluginResult<DeliveryLogsResult>.Ok(logs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reading delivery logs for topic {TopicArn} failed.", topicArn);
            return PluginResult<DeliveryLogsResult>.Fail(ex);
        }
    }
}
