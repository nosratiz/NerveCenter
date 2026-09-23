using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed record PublishCommand(Guid ConnectionId, string ConnectionName, string TopicName, string TopicArn, SnsPublishRequest Request);

public sealed class PublishCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PublishCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(PublishCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.PublishAsync(secret, cmd.TopicArn, cmd.Request, ct);
            await audit.RecordAsync("aws.topic.publish", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing to topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.topic.publish", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
