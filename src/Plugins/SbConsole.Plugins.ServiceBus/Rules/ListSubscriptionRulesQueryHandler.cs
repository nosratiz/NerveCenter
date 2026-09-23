using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed class ListSubscriptionRulesQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListSubscriptionRulesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<RuleSummary>>> HandleAsync(Guid connectionId, string topicName, string subscriptionName, CancellationToken ct = default)
    {
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<RuleSummary>>.Fail("Connection not found.");
            }

            var rules = await operations.ListRulesAsync(secret, topicName, subscriptionName, ct);
            return PluginResult<IReadOnlyList<RuleSummary>>.Ok(rules);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing rules for {TopicName}/{SubscriptionName} failed.", topicName, subscriptionName);
            return PluginResult<IReadOnlyList<RuleSummary>>.Fail(ex);
        }
    }
}
