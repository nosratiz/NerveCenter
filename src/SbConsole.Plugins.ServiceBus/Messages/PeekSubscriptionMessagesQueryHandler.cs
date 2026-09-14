using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed class PeekSubscriptionMessagesQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<PeekSubscriptionMessagesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<PeekedMessage>>> HandleAsync(
        Guid connectionId, string topicName, string subscriptionName, bool fromDeadLetter,
        long? fromSequenceNumber = null, int maxMessages = 32, CancellationToken ct = default)
    {
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<PeekedMessage>>.Fail("Connection not found.");
            }

            var messages = await operations.PeekSubscriptionMessagesAsync(secret, topicName, subscriptionName, fromDeadLetter, maxMessages, fromSequenceNumber, ct);
            return PluginResult<IReadOnlyList<PeekedMessage>>.Ok(messages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Peeking {TopicName}/{SubscriptionName} (dead-letter: {FromDeadLetter}) failed.", topicName, subscriptionName, fromDeadLetter);
            return PluginResult<IReadOnlyList<PeekedMessage>>.Fail(ex);
        }
    }
}
