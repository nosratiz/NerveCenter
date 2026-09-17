using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Messages;

public sealed class PeekMessagesQueryHandler(IKafkaOperations operations, IConnectionProvider connections, ILogger<PeekMessagesQueryHandler> logger)
{
    public async Task<PluginResult<PeekResult>> HandleAsync(
        Guid connectionId, string topicName, int partition, PeekStart start, long? offset, int maxMessages = 32, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<PeekResult>.Fail("Connection not found.");
            }

            var result = await operations.PeekMessagesAsync(secret, topicName, partition, start, offset, maxMessages, ct);
            return PluginResult<PeekResult>.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Peeking {TopicName} partition {Partition} failed.", topicName, partition);
            return PluginResult<PeekResult>.Fail(ex);
        }
    }
}
