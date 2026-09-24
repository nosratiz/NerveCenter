using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Messages;

/// <summary>
/// Move one received message out of a dead-letter queue back into one of its source queues:
/// DlqUrl/DlqName is the queue the message was received from, SourceQueueUrl one of the queues whose
/// RedrivePolicy targets it.
/// </summary>
public sealed record MoveMessageToSourceCommand(
    Guid ConnectionId, string ConnectionName, string DlqUrl, string DlqName, string SourceQueueUrl, ReceivedMessage Message);

/// <summary>
/// Send-then-delete, never the other way round: if the send fails the message is untouched in the
/// DLQ; if the delete fails after a successful send the message now exists in both queues, and the
/// error says so explicitly rather than looking like a plain failure the user might just retry.
/// </summary>
public sealed class MoveMessageToSourceCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<MoveMessageToSourceCommandHandler> logger)
{
    private const string AuditAction = "aws.message.move";

    public async Task<PluginResult> HandleAsync(MoveMessageToSourceCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.DlqName}";
        var sourceName = QueueNameFromUrl(cmd.SourceQueueUrl);
        var detail = $"{cmd.Message.MessageId} → {sourceName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.SendMessageAsync(secret, cmd.SourceQueueUrl, BuildResendRequest(cmd.Message, cmd.SourceQueueUrl), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Moving message {MessageId} from {Target} to {Source}: send failed.", cmd.Message.MessageId, target, sourceName);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync(AuditAction, target, ActionRisk.Mutating, succeeded: false, detail: $"{detail}: {friendly}", ct: ct);
            return PluginResult.Fail(friendly);
        }

        try
        {
            await operations.DeleteMessageAsync(secret, cmd.DlqUrl, cmd.Message.ReceiptHandle, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Moving message {MessageId} from {Target} to {Source}: sent, but deleting from the DLQ failed.", cmd.Message.MessageId, target, sourceName);
            var error = $"The message was copied to {sourceName} but is still in the DLQ — it may be processed twice (possible duplicate). {FriendlyAwsError.From(ex)}";
            await audit.RecordAsync(AuditAction, target, ActionRisk.Mutating, succeeded: false, detail: $"{detail}: {error}", ct: ct);
            return PluginResult.Fail(error);
        }

        await audit.RecordAsync(AuditAction, target, ActionRisk.Mutating, succeeded: true, detail: detail, ct: ct);
        return PluginResult.Ok();
    }

    // FIFO-ness is read from the destination's name: SQS requires every FIFO queue name to end in
    // ".fifo", and a DLQ and its sources are always the same type. FIFO needs a MessageGroupId (reuse
    // the original so per-group ordering is kept) and a dedup id (the original message id -- a
    // retried move within SQS's 5-minute dedup window can't double-enqueue). A standard queue gets
    // neither. Body and message attributes are copied as-is -- each attribute keeps its DataType and
    // String/Binary value (TypedMessageAttributes) -- and the delay is not (it already elapsed).
    internal static SendMessageRequest BuildResendRequest(ReceivedMessage message, string destinationQueueUrl)
    {
        var fifo = destinationQueueUrl.TrimEnd('/').EndsWith(".fifo", StringComparison.Ordinal);
        return new SendMessageRequest(
            message.Body,
            MessageAttributes: null,
            DelaySeconds: null,
            MessageGroupId: fifo ? message.MessageGroupId : null,
            MessageDeduplicationId: fifo ? message.MessageId : null,
            TypedMessageAttributes: message.MessageAttributes.Count > 0 ? message.MessageAttributes : null);
    }

    internal static string QueueNameFromUrl(string queueUrl)
    {
        var trimmed = queueUrl.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// The queues a received message can be moved back to: the DLQ's dead-letter sources, from
    /// SQS's native ListDeadLetterSourceQueues (QueueDetails.DeadLetterSourceQueueUrls) -- one call,
    /// instead of scanning every queue's RedrivePolicy. Null when that's unknown (the detail load or
    /// the sources lookup failed), empty when the queue isn't a DLQ.
    /// </summary>
    internal static IReadOnlyList<string>? ResolveSources(QueueDetails? detail) =>
        detail?.DeadLetterSourceQueueUrls?
            .Distinct(StringComparer.Ordinal)
            .OrderBy(QueueNameFromUrl, StringComparer.Ordinal)
            .ToList();
}
