using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record ResubmitSubscriptionDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, IReadOnlyList<long> SequenceNumbers);

public sealed class ResubmitSubscriptionDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ResubmitSubscriptionDeadLetterMessagesCommandHandler> logger)
{
    public async Task<PluginResult<int>> HandleAsync(ResubmitSubscriptionDeadLetterMessagesCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}";
        int resubmitted;
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult<int>.Fail("Connection not found.");
            }

            resubmitted = await operations.ResubmitSubscriptionDeadLetterMessagesAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.SequenceNumbers, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resubmitting {Count} dead-letter message(s) from {Target} failed.", cmd.SequenceNumbers.Count, target);
            await audit.RecordAsync("message.resubmit", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult<int>.Fail(ex);
        }

        await audit.RecordAsync("message.resubmit", target, ActionRisk.Mutating, succeeded: true, detail: $"{resubmitted} of {cmd.SequenceNumbers.Count} resubmitted", ct: ct);
        return PluginResult<int>.Ok(resubmitted);
    }
}
