using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record ResubmitDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string QueueName, IReadOnlyList<long> SequenceNumbers);

public sealed class ResubmitDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit)
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
            await audit.RecordAsync("message.resubmit", target, ActionRisk.Mutating, succeeded: false, detail: ex.Message, ct: ct);
            return PluginResult<int>.Fail(ex.Message);
        }

        await audit.RecordAsync("message.resubmit", target, ActionRisk.Mutating, succeeded: true, detail: $"{resubmitted} of {cmd.SequenceNumbers.Count} resubmitted", ct: ct);
        return PluginResult<int>.Ok(resubmitted);
    }
}
