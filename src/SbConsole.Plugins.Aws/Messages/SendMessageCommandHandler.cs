using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Messages;

public sealed record SendMessageCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, SendMessageRequest Request);

public sealed class SendMessageCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(SendMessageCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.SendMessageAsync(secret, cmd.QueueUrl, cmd.Request, ct);
            await audit.RecordAsync("aws.message.send", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.message.send", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
