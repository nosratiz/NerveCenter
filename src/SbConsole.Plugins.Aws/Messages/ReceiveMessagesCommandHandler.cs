using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Messages;

public sealed record ReceiveMessagesCommand(
    Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName,
    int MaxMessages, int? VisibilityTimeoutSeconds, int WaitTimeSeconds);

public sealed class ReceiveMessagesCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult<IReadOnlyList<ReceivedMessage>>> HandleAsync(ReceiveMessagesCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<ReceivedMessage>>.Fail("Connection not found.");
        }

        try
        {
            var messages = await operations.ReceiveMessagesAsync(secret, cmd.QueueUrl, cmd.MaxMessages, cmd.VisibilityTimeoutSeconds, cmd.WaitTimeSeconds, ct);
            await audit.RecordAsync("aws.queue.receive", target, ActionRisk.Mutating, succeeded: true, detail: $"{messages.Count} message(s)", ct: ct);
            return PluginResult<IReadOnlyList<ReceivedMessage>>.Ok(messages);
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.receive", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<IReadOnlyList<ReceivedMessage>>.Fail(friendly);
        }
    }
}
