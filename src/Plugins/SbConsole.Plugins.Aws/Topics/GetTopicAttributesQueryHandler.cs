using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed class GetTopicAttributesQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<GetTopicAttributesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyDictionary<string, string>>> HandleAsync(Guid connectionId, string topicArn, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyDictionary<string, string>>.Fail("Connection not found.");
            }

            var attributes = await operations.GetTopicAttributesAsync(secret, topicArn, ct);
            return PluginResult<IReadOnlyDictionary<string, string>>.Ok(attributes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Getting attributes for topic {TopicArn} failed.", topicArn);
            return PluginResult<IReadOnlyDictionary<string, string>>.Fail(ex);
        }
    }
}
