using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed record CreateQueueCommand(Guid ConnectionId, string ConnectionName, CreateQueueRequest Request);

public sealed class CreateQueueCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult<string>> HandleAsync(CreateQueueCommand cmd, CancellationToken ct = default)
    {
        var requestedTarget = $"{cmd.ConnectionName}/{cmd.Request.Name}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail("Connection not found.");
        }

        try
        {
            var queueUrl = await operations.CreateQueueAsync(secret, cmd.Request, ct);
            // A FIFO request's name gets a .fifo suffix appended during creation (SqsOperations.
            // CreateQueueAsync) -- audit the actual created name (derived from the returned URL),
            // not the pre-suffix requested one, so the audit trail names the real resource.
            var actualTarget = $"{cmd.ConnectionName}/{SqsOperations.QueueNameFromUrl(queueUrl)}";
            await audit.RecordAsync("aws.queue.create", actualTarget, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult<string>.Ok(queueUrl);
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.create", requestedTarget, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
