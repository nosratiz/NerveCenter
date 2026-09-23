using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Subscriptions;

public sealed class ListSubscriptionsQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<ListSubscriptionsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<SubscriptionSummary>>> HandleAsync(Guid connectionId, string topicArn, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail("Connection not found.");
            }

            var subscriptions = await operations.ListSubscriptionsAsync(secret, topicArn, ct);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Ok(subscriptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing subscriptions for topic {TopicArn} failed.", topicArn);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail(ex);
        }
    }
}
