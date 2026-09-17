using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.ConsumerGroups;

public sealed class ListConsumerGroupsQueryHandler(IKafkaOperations operations, IConnectionProvider connections, ILogger<ListConsumerGroupsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<ConsumerGroupSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<ConsumerGroupSummary>>.Fail("Connection not found.");
            }

            var groups = await operations.ListConsumerGroupsAsync(secret, ct);
            return PluginResult<IReadOnlyList<ConsumerGroupSummary>>.Ok(groups);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing consumer groups for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<ConsumerGroupSummary>>.Fail(ex);
        }
    }
}
