using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Subscriptions;

public sealed record CreateSubscriptionCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, int MaxDeliveryCount);

public sealed class CreateSubscriptionCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateSubscriptionCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateSubscriptionCommand cmd, CancellationToken ct = default)
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

            await operations.CreateSubscriptionAsync(secret, cmd.TopicName, new CreateSubscriptionRequest(cmd.SubscriptionName, cmd.MaxDeliveryCount), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating subscription {Target} failed.", target);
            await audit.RecordAsync("subscription.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("subscription.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
