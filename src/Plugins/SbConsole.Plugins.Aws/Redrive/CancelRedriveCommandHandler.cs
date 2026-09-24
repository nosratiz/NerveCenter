using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Redrive;

public sealed record CancelRedriveCommand(Guid ConnectionId, string ConnectionName, string TaskHandle, string SourceQueueName);

public sealed class CancelRedriveCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CancelRedriveCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CancelRedriveCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.SourceQueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.CancelMessageMoveTaskAsync(secret, cmd.TaskHandle, ct);
            await audit.RecordAsync("aws.queue.redrive.cancel", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cancelling redrive task for {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.redrive.cancel", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
