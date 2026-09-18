using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.ConsumerGroups;

public sealed class GetConsumerGroupDetailQueryHandler(IKafkaOperations operations, IConnectionProvider connections, ILogger<GetConsumerGroupDetailQueryHandler> logger)
{
    public async Task<PluginResult<ConsumerGroupDetail>> HandleAsync(Guid connectionId, string groupId, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<ConsumerGroupDetail>.Fail("Connection not found.");
            }

            var detail = await operations.GetConsumerGroupDetailAsync(secret, groupId, ct);
            return PluginResult<ConsumerGroupDetail>.Ok(detail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Describing consumer group {GroupId} failed.", groupId);
            return PluginResult<ConsumerGroupDetail>.Fail(ex);
        }
    }
}
