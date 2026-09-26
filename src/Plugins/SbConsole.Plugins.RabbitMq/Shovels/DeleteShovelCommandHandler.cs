using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Shovels;

public sealed record DeleteShovelCommand(Guid ConnectionId, string ConnectionName, string Vhost, string Name);

public sealed class DeleteShovelCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteShovelCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(DeleteShovelCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.shovel.delete", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Name), ActionRisk.Destructive, null,
            secret => operations.DeleteShovelAsync(secret, cmd.Vhost, cmd.Name, ct), ct);
}
