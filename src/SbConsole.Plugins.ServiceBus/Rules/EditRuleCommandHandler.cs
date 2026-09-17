using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Rules;

public sealed record EditRuleCommand(
    Guid ConnectionId, string ConnectionName, bool IsProd,
    string TopicName, string SubscriptionName, string OriginalName, CreateRuleRequest NewRule);

public sealed class EditRuleCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<EditRuleCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(EditRuleCommand cmd, CancellationToken ct = default)
    {
        string? secret;
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resolving the connection secret for an edit of rule {OriginalName} failed.", cmd.OriginalName);
            return PluginResult.Fail(ex);
        }

        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        return cmd.NewRule.Name == cmd.OriginalName
            ? await DeleteThenCreateAsync(cmd, secret, ct)
            : await CreateThenDeleteAsync(cmd, secret, ct);
    }

    private async Task<PluginResult> DeleteThenCreateAsync(EditRuleCommand cmd, string secret, CancellationToken ct)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.OriginalName}";
        try
        {
            await operations.DeleteRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.OriginalName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting rule {Target} (for edit) failed.", target);
            await audit.RecordAsync("rule.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("rule.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);

        try
        {
            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.NewRule, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating rule {Target} (for edit) failed after the original was deleted.", target);
            await audit.RecordAsync("rule.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail($"Rule '{cmd.OriginalName}' was deleted but the update could not be created ({FriendlyError.From(ex)}) — it no longer exists and must be re-added.");
        }

        await audit.RecordAsync("rule.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }

    private async Task<PluginResult> CreateThenDeleteAsync(EditRuleCommand cmd, string secret, CancellationToken ct)
    {
        var newTarget = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.NewRule.Name}";
        var oldTarget = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}/{cmd.OriginalName}";
        try
        {
            await operations.CreateRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.NewRule, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating rule {Target} (for edit/rename) failed.", newTarget);
            await audit.RecordAsync("rule.create", newTarget, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("rule.create", newTarget, ActionRisk.Mutating, succeeded: true, ct: ct);

        try
        {
            await operations.DeleteRuleAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.OriginalName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting rule {Target} (for edit/rename) failed after the new rule was created.", oldTarget);
            await audit.RecordAsync("rule.delete", oldTarget, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail($"A new rule '{cmd.NewRule.Name}' was created, but the old rule '{cmd.OriginalName}' could not be removed ({FriendlyError.From(ex)}) and must be deleted manually.");
        }

        await audit.RecordAsync("rule.delete", oldTarget, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
