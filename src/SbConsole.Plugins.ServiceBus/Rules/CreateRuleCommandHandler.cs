using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed record CreateRuleCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, CreateRuleRequest Rule);

public sealed class CreateRuleCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateRuleCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateRuleCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.Rule.Name}";
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.Rule, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating rule {Target} failed.", target);
            await audit.RecordAsync("rule.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("rule.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
