using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Redrive;

public sealed record StartRedriveCommand(
    Guid ConnectionId, string ConnectionName, string SourceQueueArn, string SourceQueueName,
    string DestinationQueueArn, int? MaxMessagesPerSecond);

public sealed class StartRedriveCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<StartRedriveCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(StartRedriveCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.SourceQueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.StartRedriveTaskAsync(secret, cmd.SourceQueueArn, cmd.DestinationQueueArn, cmd.MaxMessagesPerSecond, ct);
            await audit.RecordAsync("aws.queue.redrive", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Starting redrive for {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.redrive", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
