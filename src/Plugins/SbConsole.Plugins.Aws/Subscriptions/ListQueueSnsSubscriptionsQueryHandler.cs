using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Subscriptions;

/// <summary>SNS subscriptions (any topic) delivering to the given queue ARN -- the queue detail page's SNS panel.</summary>
public sealed class ListQueueSnsSubscriptionsQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<ListQueueSnsSubscriptionsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<SubscriptionSummary>>> HandleAsync(Guid connectionId, string queueArn, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail("Connection not found.");
            }

            var subscriptions = await operations.ListSubscriptionsForEndpointAsync(secret, queueArn, ct);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Ok(subscriptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing SNS subscriptions for queue {QueueArn} failed.", queueArn);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail(ex);
        }
    }
}
