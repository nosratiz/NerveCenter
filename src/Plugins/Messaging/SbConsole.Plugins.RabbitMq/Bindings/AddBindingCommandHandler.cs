using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Bindings;

public sealed record AddBindingCommand(
    Guid ConnectionId, string ConnectionName, string Vhost, string Exchange, string Destination, string RoutingKey,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    BindingDestinationType DestinationType = BindingDestinationType.Queue);

public sealed class AddBindingCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<AddBindingCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(AddBindingCommand cmd, CancellationToken ct = default)
    {
        var arguments = cmd.Arguments ?? new Dictionary<string, object?>();
        return HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.binding.add", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, $"{cmd.Exchange}→{cmd.Destination}"), ActionRisk.Mutating, $"key {cmd.RoutingKey}",
            secret => cmd.DestinationType == BindingDestinationType.Exchange
                ? operations.AddExchangeBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Destination, cmd.RoutingKey, arguments, ct)
                : operations.AddBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Destination, cmd.RoutingKey, arguments, ct), ct);
    }
}
