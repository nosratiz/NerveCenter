using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Messages;

public sealed record DeleteMessageCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, string ReceiptHandle);

public sealed class DeleteMessageCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteMessageCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteMessageCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.DeleteMessageAsync(secret, cmd.QueueUrl, cmd.ReceiptHandle, ct);
            await audit.RecordAsync("aws.message.delete", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting message from {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.message.delete", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
