using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.DeadLetter;

public sealed record DeadLetterOverviewEntry(
    Guid ConnectionId, string ConnectionName, string DlqTopicName, string OriginalTopicName,
    int PartitionCount, long ApproximateMessageCount);

/// <summary>
/// Unlike every other handler in this plugin, this one takes no connectionId -- dead-letter
/// monitoring is a cross-connection "check everything at once" task, same shape Service Bus's own
/// dead-letter overview handler uses. No PluginResult wrapper: deliberately best-effort per
/// connection (one unreachable cluster shouldn't blank the whole page) -- see design spec §5.
/// </summary>
public sealed class ListDeadLetterOverviewQueryHandler(
    IKafkaOperations operations, IConnectionProvider connections, ILogger<ListDeadLetterOverviewQueryHandler> logger)
{
    public async Task<IReadOnlyList<DeadLetterOverviewEntry>> HandleAsync(CancellationToken ct = default)
    {
        var kafkaConnections = await connections.ListAsync("kafka", ct);
        var result = new List<DeadLetterOverviewEntry>();
        foreach (var connection in kafkaConnections)
        {
            try
            {
                var secret = await connections.GetSecretAsync(connection.Id, ct);
                if (secret is null)
                {
                    continue;
                }

                var topics = await operations.ListDeadLetterTopicsAsync(secret, ct);
                result.AddRange(topics.Select(t => new DeadLetterOverviewEntry(
                    connection.Id, connection.Name, t.DlqTopicName, t.OriginalTopicName, t.PartitionCount, t.ApproximateMessageCount)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Listing dead-letter topics for connection {ConnectionId} failed; skipping it.", connection.Id);
            }
        }

        return result;
    }
}
