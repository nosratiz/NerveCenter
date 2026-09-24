using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Redrive;

public sealed class ListRedriveTasksQueryHandler(ISqsOperations operations, IConnectionProvider connections, ILogger<ListRedriveTasksQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<MessageMoveTaskSummary>>> HandleAsync(Guid connectionId, string sourceQueueArn, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<MessageMoveTaskSummary>>.Fail("Connection not found.");
            }

            var tasks = await operations.ListMessageMoveTasksAsync(secret, sourceQueueArn, ct);
            return PluginResult<IReadOnlyList<MessageMoveTaskSummary>>.Ok(tasks);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing redrive tasks for {SourceArn} failed.", sourceQueueArn);
            return PluginResult<IReadOnlyList<MessageMoveTaskSummary>>.Fail(ex);
        }
    }
}
