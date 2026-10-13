using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Keys;

public sealed record IncrementSortedSetScoreCommand(Guid ConnectionId, string ConnectionName, int Database, string Key, string Member, double By);

/// <summary>Non-standard shape: the caller needs the new score back, so this writes its own audit row instead of using HandlerRunner.CommandAsync's no-return-value Func.</summary>
public sealed class IncrementSortedSetScoreCommandHandler(
    IRedisOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<IncrementSortedSetScoreCommandHandler> logger) : IScopedService
{
    public async Task<PluginResult<double>> HandleAsync(IncrementSortedSetScoreCommand cmd, CancellationToken ct = default)
    {
        var secret = await HandlerRunner.ResolveSecretAsync(connections, cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<double>.Fail(HandlerRunner.ConnectionNotFound);
        }

        var target = HandlerRunner.Target(cmd.ConnectionName, cmd.Database, cmd.Key);
        try
        {
            var newScore = await operations.IncrementSortedSetScoreAsync(secret, cmd.Database, cmd.Key, cmd.Member, cmd.By, ct);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.zset.incr", target, ActionRisk.Mutating, succeeded: true,
                string.Create(CultureInfo.InvariantCulture, $"{cmd.Member} by {cmd.By} -> {newScore}"));
            return PluginResult<double>.Ok(newScore);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "redis.zset.incr on {Target} failed.", target);
            var friendly = FriendlyRedisError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, "redis.zset.incr", target, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult<double>.Fail(friendly);
        }
    }
}
