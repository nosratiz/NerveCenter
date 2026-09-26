using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Shovels;

public sealed record CreateShovelCommand(Guid ConnectionId, string ConnectionName, string Vhost, CreateShovelRequest Request);

public sealed class CreateShovelCommandHandler(IRabbitOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateShovelCommandHandler> logger)
{
    public Task<PluginResult> HandleAsync(CreateShovelCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "rabbitmq.shovel.create", HandlerRunner.Target(cmd.ConnectionName, cmd.Vhost, cmd.Request.Name), ActionRisk.Mutating, null,
            secret => operations.CreateShovelAsync(secret, cmd.Vhost, cmd.Request, ct), ct);
}
