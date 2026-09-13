using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.DeadLetter;

public sealed record DeadLetterOverviewEntry(Guid ConnectionId, string ConnectionName, string EntityType, string? TopicName, string EntityName, long Count);

/// <summary>
/// Spans every azure-servicebus connection at once (docs/design.md §6.3) -- unlike every other
/// handler in this plugin, which operates on one connection named by the caller. Deliberately does
/// not return a PluginResult: there is no single "the operation failed" outcome to represent when
/// the whole point is "show me everything, best effort" -- a connection whose secret is missing or
/// whose Azure call throws is skipped and logged, never fatal to the rest of the page.
/// </summary>
public sealed class ListDeadLetterOverviewQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListDeadLetterOverviewQueryHandler> logger)
{
    public async Task<IReadOnlyList<DeadLetterOverviewEntry>> HandleAsync(CancellationToken ct = default)
    {
        var results = new List<DeadLetterOverviewEntry>();
        var allConnections = await connections.ListAsync("azure-servicebus", ct);
        foreach (var connection in allConnections)
        {
            var secret = await connections.GetSecretAsync(connection.Id, ct);
            if (secret is null)
            {
                continue;
            }

            try
            {
                var entries = await operations.ListDeadLetterEntriesAsync(secret, ct);
                results.AddRange(entries.Select(e => new DeadLetterOverviewEntry(connection.Id, connection.Name, e.EntityType, e.TopicName, e.EntityName, e.Count)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Listing dead-letter entries for connection {ConnectionName} failed; skipping it for this overview.", connection.Name);
            }
        }

        return results;
    }
}
