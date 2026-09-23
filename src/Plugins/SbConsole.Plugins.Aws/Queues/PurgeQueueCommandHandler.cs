using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed record PurgeQueueCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName);

public sealed class PurgeQueueCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PurgeQueueCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(PurgeQueueCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.PurgeQueueAsync(secret, cmd.QueueUrl, ct);
            await audit.RecordAsync("aws.queue.purge", target, ActionRisk.Destructive, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Purging queue {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.purge", target, ActionRisk.Destructive, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
