using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Messages;

public sealed record PublishMessageCommand(Guid ConnectionId, string ConnectionName, string Vhost, PublishRequest Request);

/// <summary>
/// One AMQP publish (mandatory + confirms). A basic.return is not an exception -- the result is
/// Ok(Unroutable) so the dialog can say so in place -- but it IS audited as a failed publish: the
/// user asked for a message to be delivered and it wasn't.
/// </summary>
public sealed class PublishMessageCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PublishMessageCommandHandler> logger)
{
    public const string Action = "rabbitmq.message.publish";

    public async Task<PluginResult<PublishOutcome>> HandleAsync(PublishMessageCommand cmd, CancellationToken ct = default)
    {
        var target = HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, ExchangeLabel(cmd.Request.Exchange));
        var detail = $"key {cmd.Request.RoutingKey}";
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<PublishOutcome>.Fail(HandlerRunner.ConnectionNotFound);
        }

        try
        {
            var outcome = await operations.PublishAsync(secret, cmd.Vhost, cmd.Request, ct);
            var routed = outcome == PublishOutcome.Routed;
            await audit.RecordAsync(Action, target, ActionRisk.Mutating, succeeded: routed,
                detail: routed ? detail : $"{detail} · unroutable — returned by broker", ct: ct);
            return PluginResult<PublishOutcome>.Ok(outcome);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing to {Target} failed.", target);
            var friendly = FriendlyRabbitError.From(ex);
            await audit.RecordAsync(Action, target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<PublishOutcome>.Fail(friendly);
        }
    }

    internal static string ExchangeLabel(string exchange) => exchange.Length == 0 ? "(default)" : exchange;
}
