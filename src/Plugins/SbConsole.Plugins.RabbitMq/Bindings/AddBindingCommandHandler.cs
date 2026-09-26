using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Bindings;

public sealed record AddBindingCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Exchange, string Queue, string RoutingKey, IReadOnlyDictionary<string, object?>? Arguments = null);

public sealed class AddBindingCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<AddBindingCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(AddBindingCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.binding.add", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, $"{cmd.Exchange}→{cmd.Queue}"), ActionRisk.Mutating, $"key {cmd.RoutingKey}",
            secret => operations.AddBindingAsync(secret, cmd.Vhost, cmd.Exchange, cmd.Queue, cmd.RoutingKey, cmd.Arguments ?? new Dictionary<string, object?>(), ct), ct);
}
