using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

/// <summary>Thin wrapper over ISnsOperations.ListSubscriptionsAsync for the Publish dialog's
/// fan-out preview -- SubscriptionSummary already carries each subscription's filter policy
/// (Task 4), so this exists to give the Publish dialog's own dependency a name that reads clearly
/// at its call site rather than reusing Subscriptions.ListSubscriptionsQueryHandler by cross-folder
/// reference.</summary>
public sealed class GetSubscriptionFilterPoliciesQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<GetSubscriptionFilterPoliciesQueryHandler> logger)
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
            logger.LogError(ex, "Resolving subscription filter policies for topic {TopicArn} failed.", topicArn);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail(ex);
        }
    }
}
