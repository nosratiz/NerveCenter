using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Subscriptions;

public sealed record DeleteSubscriptionCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName, string SubscriptionName);

public sealed class DeleteSubscriptionCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteSubscriptionCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteSubscriptionCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}";
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.DeleteSubscriptionAsync(secret, cmd.TopicName, cmd.SubscriptionName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting subscription {Target} failed.", target);
            await audit.RecordAsync("subscription.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("subscription.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
