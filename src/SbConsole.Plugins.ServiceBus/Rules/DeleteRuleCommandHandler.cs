using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed record DeleteRuleCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName, string SubscriptionName, string RuleName);

public sealed class DeleteRuleCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteRuleCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteRuleCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.RuleName}";
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.DeleteRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.RuleName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting rule {Target} failed.", target);
            await audit.RecordAsync("rule.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("rule.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
