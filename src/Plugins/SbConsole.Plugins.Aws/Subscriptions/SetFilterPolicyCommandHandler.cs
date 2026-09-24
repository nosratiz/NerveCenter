using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Subscriptions;

/// <summary>A null/empty <paramref name="PolicyJson"/> clears the subscription's filter policy.</summary>
public sealed record SetFilterPolicyCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionArn, string? PolicyJson, string Scope);

/// <summary>
/// Mutating, not Destructive -- a filter policy is fully reversible by setting it again. Input
/// that fails FilterPolicyValidator is rejected before the secret is fetched or AWS is called,
/// and isn't audited (nothing was attempted); every AWS attempt is audited, success or failure.
/// </summary>
public sealed class SetFilterPolicyCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SetFilterPolicyCommandHandler> logger)
{
    private const string AuditAction = "aws.subscription.filterpolicy.set";

    public async Task<PluginResult> HandleAsync(SetFilterPolicyCommand cmd, CancellationToken ct = default)
    {
        var validation = FilterPolicyValidator.Validate(cmd.PolicyJson, cmd.Scope);
        if (!validation.IsValid)
        {
            return PluginResult.Fail(validation.Error!);
        }

        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            var policy = validation.IsClear ? null : cmd.PolicyJson!.Trim();
            await operations.SetSubscriptionFilterPolicyAsync(secret, cmd.SubscriptionArn, policy, cmd.Scope, ct);
            await audit.RecordAsync(AuditAction, target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (FilterPolicyPartiallyAppliedException ex)
        {
            // Half of a two-call scope change landed -- say so explicitly (and audit it) rather than
            // report a plain failure the user might assume changed nothing.
            logger.LogError(ex, "Setting the filter policy on a subscription of topic {Target} partially failed.", target);
            var error = $"{ex.Message} {FriendlyAwsError.From(ex.InnerException!)}";
            await audit.RecordAsync(AuditAction, target, ActionRisk.Mutating, succeeded: false, detail: error, ct: ct);
            return PluginResult.Fail(error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setting the filter policy on a subscription of topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync(AuditAction, target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
