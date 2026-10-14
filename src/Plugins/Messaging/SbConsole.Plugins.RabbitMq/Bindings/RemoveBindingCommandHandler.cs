using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Bindings;

public sealed record RemoveBindingCommand(
    Guid ConnectionId, string ConnectionName, string Vhost, string Exchange, string Destination, string RoutingKey, string PropertiesKey,
    BindingDestinationType DestinationType = BindingDestinationType.Queue);

public sealed class RemoveBindingCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RemoveBindingCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(RemoveBindingCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.binding.remove", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, $"{cmd.Exchange}→{cmd.Destination}"), ActionRisk.Mutating, $"key {cmd.RoutingKey}",
            secret => cmd.DestinationType == BindingDestinationType.Exchange
                ? operations.RemoveExchangeBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Destination, cmd.PropertiesKey, ct)
                : operations.RemoveBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Destination, cmd.PropertiesKey, ct), ct);
}
