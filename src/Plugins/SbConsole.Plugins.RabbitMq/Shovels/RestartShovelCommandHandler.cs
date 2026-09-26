using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Shovels;

public sealed record RestartShovelCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Name);

public sealed class RestartShovelCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<RestartShovelCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(RestartShovelCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.shovel.restart", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Name), ActionRisk.Mutating, null,
            secret => operations.RestartShovelAsync(secret, cmd.Vhost, cmd.Name, ct), ct);
}
