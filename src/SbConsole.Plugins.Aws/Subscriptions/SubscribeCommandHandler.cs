using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Subscriptions;

public sealed record SubscribeCommand(Guid ConnectionId, string ConnectionName, string TopicName, SubscribeRequest Request);

/// <summary>Also used for "Resend" on a pending subscription -- calling this again with the same
/// topic/protocol/endpoint is the only mechanism AWS exposes for redelivering a confirmation.</summary>
public sealed class SubscribeCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SubscribeCommandHandler> logger)
{
    public async Task<PluginResult<string>> HandleAsync(SubscribeCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail("Connection not found.");
        }

        try
        {
            var subscriptionArn = await operations.SubscribeAsync(secret, cmd.Request, ct);
            await audit.RecordAsync("aws.subscription.subscribe", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult<string>.Ok(subscriptionArn);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subscribing to topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.subscription.subscribe", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
