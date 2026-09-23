using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed record CreateTopicCommand(Guid ConnectionId, string ConnectionName, CreateTopicRequest Request);

public sealed class CreateTopicCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateTopicCommandHandler> logger)
{
    public async Task<PluginResult<string>> HandleAsync(CreateTopicCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Request.Name}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail("Connection not found.");
        }

        try
        {
            var topicArn = await operations.CreateTopicAsync(secret, cmd.Request, ct);
            await audit.RecordAsync("aws.topic.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult<string>.Ok(topicArn);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.topic.create", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
