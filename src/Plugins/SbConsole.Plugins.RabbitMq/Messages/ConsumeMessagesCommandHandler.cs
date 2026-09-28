using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Messages;

public sealed record ConsumeMessagesCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Queue, int Count);

/// <summary>basic.get + ack: every message read is removed from the queue. Destructive and audited.</summary>
public sealed class ConsumeMessagesCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ConsumeMessagesCommandHandler> logger)
{
    private const string Action = "rabbitmq.message.consume";

    public async Task<PluginResult<IReadOnlyList<RabbitMessage>>> HandleAsync(ConsumeMessagesCommand cmd, CancellationToken ct = default)
    {
        var target = HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Queue);
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<RabbitMessage>>.Fail(HandlerRunner.ConnectionNotFound);
        }

        IReadOnlyList<RabbitMessage> messages;
        try
        {
            messages = await operations.GetMessagesAsync(secret, cmd.Vhost, cmd.Queue, cmd.Count, GetMode.Consume, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Consuming from {Target} failed.", target);
            var friendly = FriendlyRabbitError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Destructive, succeeded: false, friendly);
            return PluginResult<IReadOnlyList<RabbitMessage>>.Fail(friendly);
        }

        // The messages are acked -- they now exist only in this result. Nothing after this point
        // (audit failure, cancellation) may stop them reaching the caller.
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Destructive, succeeded: true, $"{messages.Count} consumed");
        return PluginResult<IReadOnlyList<RabbitMessage>>.Ok(messages);
    }
}
