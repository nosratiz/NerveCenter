using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Streams;

public sealed record DeleteStreamConsumerCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Group, string Consumer);

/// <summary>XGROUP DELCONSUMER -- Destructive: any entries still pending on the consumer leave the PEL with it, so the page offers "claim to a live consumer" first (mockup 1d).</summary>
public sealed class DeleteStreamConsumerCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteStreamConsumerCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(DeleteStreamConsumerCommand cmd, CancellationToken ct = default) =>
        HandlerRunner.CommandAsync(connections, audit, logger, cmd.ConnectionId,
            "redis.stream.consumer.delete", HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key), ActionRisk.Destructive,
            string.Create(CultureInfo.InvariantCulture, $"group {cmd.Group}: consumer {cmd.Consumer}"),
            secret => operations.DeleteStreamConsumerAsync(secret, cmd.Database, cmd.Key, cmd.Group, cmd.Consumer, ct), ct);
}
