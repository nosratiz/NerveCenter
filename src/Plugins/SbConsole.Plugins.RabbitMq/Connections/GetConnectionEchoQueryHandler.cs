using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Connections;

/// <summary>The allowlisted secret echo for the page header, plus the connection's configured vhost (the picker's default).</summary>
public sealed record ConnectionEcho(string Echo, string DefaultVhost);

public sealed class GetConnectionEchoQueryHandler(IConnectionProvider connections, ILogger<GetConnectionEchoQueryHandler> logger)
{
    public Task<PluginResult<ConnectionEcho>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Resolving the connection echo",
            secret => Task.FromResult(new ConnectionEcho(RabbitConfigParser.SafeEcho(secret), RabbitConnectionSettings.From(secret).Vhost)), ct);
}
