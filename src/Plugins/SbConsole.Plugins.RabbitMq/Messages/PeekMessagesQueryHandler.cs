using Microsoft.Extensions.Logging;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Messages;

/// <summary>
/// basic.get + nack-requeue. A query (no audit row) even though the broker marks each message
/// redelivered -- that side effect is disclosed in the Get page's byline instead (design spec §8).
/// </summary>
public sealed class PeekMessagesQueryHandler(IRabbitOperations operations, IConnectionProvider connections, ILogger<PeekMessagesQueryHandler> logger)
{
    public Task<PluginResult<IReadOnlyList<RabbitMessage>>> HandleAsync(Guid connectionId, string vhost, string queue, int count, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "Peeking messages",
            secret => operations.GetMessagesAsync(secret, vhost, queue, count, GetMode.Peek, ct), ct);
}
