using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record ResubmitDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string QueueName, IReadOnlyList<long> SequenceNumbers);

public sealed class ResubmitDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ResubmitDeadLetterMessagesCommandHandler> logger)
{
    public async Task<PluginResult<int>> HandleAsync(ResubmitDeadLetterMessagesCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<int>.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        int resubmitted;
        try
        {
            resubmitted = await operations.ResubmitDeadLetterMessagesAsync(secret, cmd.QueueName, cmd.SequenceNumbers, ct);
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
