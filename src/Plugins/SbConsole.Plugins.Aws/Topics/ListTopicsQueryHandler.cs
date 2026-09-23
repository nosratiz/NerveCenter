using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed class ListTopicsQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<ListTopicsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<TopicSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<TopicSummary>>.Fail("Connection not found.");
            }

            var topics = await operations.ListTopicsAsync(secret, ct);
            return PluginResult<IReadOnlyList<TopicSummary>>.Ok(topics);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing topics for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<TopicSummary>>.Fail(ex);
        }
    }
}
