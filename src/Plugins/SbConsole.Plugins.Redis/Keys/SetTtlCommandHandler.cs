using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record SetTtlCommand(Guid ConnectionId, string ConnectionName, SetTtlRequest Request);

public sealed class SetTtlCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SetTtlCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(SetTtlCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.key.setttl", HandlerRunner.Target(cmd.ConnectionName, cmd.Request.Database, cmd.Request.Key), ActionRisk.Mutating, DetailFor(cmd.Request),
            secret => operations.SetTtlAsync(secret, cmd.Request, ct), ct);

    private static string? DetailFor(SetTtlRequest request) => request switch
    {
        { Persist: true } => "persist",
        { ExpireIn: { } ttl } => string.Create(CultureInfo.InvariantCulture, $"expires in {ttl}"),
        { ExpireAt: { } at } => string.Create(CultureInfo.InvariantCulture, $"expires at {at:O}"),
        _ => null,
    };
}
