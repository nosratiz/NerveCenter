using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Query;

public sealed record CheckpointWalCommand(Guid ConnectionId, string ConnectionName);

/// <summary>PRAGMA wal_checkpoint(TRUNCATE) after a write (design spec §11.4). Mutating: it rewrites the main file.</summary>
public sealed class CheckpointWalCommandHandler(
    ISqliteOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CheckpointWalCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.wal.checkpoint";

    public async Task<PluginResult<WalCheckpointResult>> HandleAsync(CheckpointWalCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<WalCheckpointResult>.Fail(secretError!);
        }

        WalCheckpointResult result;
        try
        {
            result = await operations.CheckpointWalAsync(secret, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: false, friendly);
            return PluginResult<WalCheckpointResult>.Fail(friendly);
        }

        var detail = string.Create(CultureInfo.InvariantCulture,
            $"{(result.Busy ? "busy" : "ok")} · {result.LogFrames} frames · {result.CheckpointedFrames} checkpointed");
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Mutating, succeeded: true, detail);
        return PluginResult<WalCheckpointResult>.Ok(result);
    }
}
