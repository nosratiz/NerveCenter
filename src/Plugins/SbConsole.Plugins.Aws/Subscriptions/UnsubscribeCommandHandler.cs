using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Subscriptions;

public sealed record UnsubscribeCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionArn);

public sealed class UnsubscribeCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<UnsubscribeCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(UnsubscribeCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.UnsubscribeAsync(secret, cmd.SubscriptionArn, ct);
            // Mutating, not Destructive -- re-subscribing fully reverses this, unlike deleting a topic.
            await audit.RecordAsync("aws.subscription.unsubscribe", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unsubscribing from topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.subscription.unsubscribe", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
