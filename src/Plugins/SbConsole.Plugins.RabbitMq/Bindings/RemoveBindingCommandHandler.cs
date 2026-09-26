using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Bindings;

public sealed record RemoveBindingCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Exchange, string Queue, string RoutingKey, string PropertiesKey);

public sealed class RemoveBindingCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RemoveBindingCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(RemoveBindingCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.binding.remove", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, $"{cmd.Exchange}→{cmd.Queue}"), ActionRisk.Mutating, $"key {cmd.RoutingKey}",
            secret => operations.RemoveBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Queue, cmd.PropertiesKey, ct), ct);
}
