using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Streams;

public sealed record AddStreamEntryCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, IReadOnlyList<StreamFieldValue> Fields, long? MaxLen = null);

/// <summary>
/// Non-standard shape (like DeleteByPatternCommandHandler): the caller needs the server-generated
/// entry id back, not just success/failure, so this rolls its own ResolveSecretAsync/
/// AuditQuietlyAsync flow instead of HandlerRunner.CommandAsync.
/// </summary>
public sealed class AddStreamEntryCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<AddStreamEntryCommandHandler> logger)
{
    public async Task<PluginResult<string>> HandleAsync(AddStreamEntryCommand cmd, CancellationToken ct = default)
    {
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail(HandlerRunner.ConnectionNotFound);
        }

        var target = HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key);
        try
        {
            var id = await operations.AddStreamEntryAsync(secret, cmd.Database, cmd.Key, cmd.Fields, cmd.MaxLen, ct);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.stream.add", target, ActionRisk.Mutating, succeeded: true, id);
            return PluginResult<string>.Ok(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "redis.stream.add on {Target} failed.", target);
            var friendly = FriendlyRedisError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.stream.add", target, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
