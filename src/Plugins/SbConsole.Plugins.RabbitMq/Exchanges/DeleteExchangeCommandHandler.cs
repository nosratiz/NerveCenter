using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Exchanges;

public sealed record DeleteExchangeCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Exchange);

public sealed class DeleteExchangeCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteExchangeCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(DeleteExchangeCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.exchange.delete", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Exchange), ActionRisk.Destructive, null,
            secret => operations.DeleteExchangeAsync(secret, cmd.Vhost, cmd.Exchange, ct), ct);
}
