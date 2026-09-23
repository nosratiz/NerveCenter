using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Topics;

/// <summary>A topic plus its subscriptions' counts summed into ActiveMessageCount/
/// DeadLetterMessageCount -- so a collapsed topic row can show real aggregate numbers
/// (docs/design.md §6.2) without the caller having to sum Subscriptions itself.</summary>
public sealed record TopicRow(TopicSummary Topic, long ActiveMessageCount, long DeadLetterMessageCount, IReadOnlyList<SubscriptionSummary> Subscriptions);

public sealed class ListTopicsQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListTopicsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<TopicRow>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<TopicRow>>.Fail("Connection not found.");
            }

            var topics = await operations.ListTopicsAsync(secret, ct);
            var rows = new List<TopicRow>();
            foreach (var topic in topics)
            {
                var subscriptions = await operations.ListSubscriptionsAsync(secret, topic.Name, ct);
                var active = subscriptions.Sum(s => s.ActiveMessageCount);
                var deadLetter = subscriptions.Sum(s => s.DeadLetterMessageCount);
                rows.Add(new TopicRow(topic, active, deadLetter, subscriptions));
            }

            return PluginResult<IReadOnlyList<TopicRow>>.Ok(rows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing topics for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<TopicRow>>.Fail(ex);
        }
    }
}
