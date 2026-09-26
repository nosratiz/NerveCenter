using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Messages;

public sealed record RepublishMessagesCommand(
    Guid ConnectionId, string ConnectionName, string Vhost, string SourceQueue, string Label, IReadOnlyList<PublishRequest> Requests);

public sealed record RepublishResult(int Routed, int Unroutable);

/// <summary>
/// Requeue / Republish from the Get page: publishes each message in order, stops at the first
/// exception, and writes ONE audit row for the batch (a per-message row would bury the log).
/// Label says which UI action it was ("requeue" or "republish").
/// </summary>
public sealed class RepublishMessagesCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RepublishMessagesCommandHandler> logger)
{
    public async Task<PluginResult<RepublishResult>> HandleAsync(RepublishMessagesCommand cmd, CancellationToken ct = default)
    {
        var target = HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.SourceQueue);
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<RepublishResult>.Fail(HandlerRunner.ConnectionNotFound);
        }

        var routed = 0;
        var unroutable = 0;
        try
        {
            foreach (var request in cmd.Requests)
            {
                if (await operations.PublishAsync(secret, cmd.Vhost, request, ct) == PublishOutcome.Routed)
                {
                    routed++;
                }
                else
                {
                    unroutable++;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Label} from {Target} failed after {Count} messages.", cmd.Label, target, routed + unroutable);
            var friendly = FriendlyRabbitError.From(ex);
            await audit.RecordAsync(PublishMessageCommandHandler.Action, target, ActionRisk.Mutating, succeeded: false,
                detail: $"{cmd.Label}: {routed} routed, {unroutable} unroutable, then failed — {friendly}", ct: ct);
            return PluginResult<RepublishResult>.Fail(
                routed + unroutable == 0 ? friendly : $"{friendly} ({routed + unroutable} of {cmd.Requests.Count} were published first)");
        }

        await audit.RecordAsync(PublishMessageCommandHandler.Action, target, ActionRisk.Mutating, succeeded: unroutable == 0,
            detail: $"{cmd.Label}: {routed} routed, {unroutable} unroutable", ct: ct);
        return PluginResult<RepublishResult>.Ok(new RepublishResult(routed, unroutable));
    }
}
