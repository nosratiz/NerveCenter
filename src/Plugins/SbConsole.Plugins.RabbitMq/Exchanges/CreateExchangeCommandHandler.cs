using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Exchanges;

public sealed record CreateExchangeCommand(Guid ConnectionId, string ConnectionName, string Vhost, CreateExchangeRequest Request);

public sealed class CreateExchangeCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateExchangeCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(CreateExchangeCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.exchange.create", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Request.Name), ActionRisk.Mutating, cmd.Request.Type,
            secret => operations.CreateExchangeAsync(secret, cmd.Vhost, cmd.Request, ct), ct);
}
