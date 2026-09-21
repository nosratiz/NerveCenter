using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed record CreateQueueCommand(Guid ConnectionId, string ConnectionName, CreateQueueRequest Request);

public sealed class CreateQueueCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult<string>> HandleAsync(CreateQueueCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Request.Name}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail("Connection not found.");
        }

        try
        {
            var queueUrl = await operations.CreateQueueAsync(secret, cmd.Request, ct);
            await audit.RecordAsync("aws.queue.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult<string>.Ok(queueUrl);
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.create", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
