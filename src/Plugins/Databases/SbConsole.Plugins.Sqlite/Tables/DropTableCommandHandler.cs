using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tables;

/// <summary>Actor is the person (from the page's auth state); it goes into the backup record when BackupFirst is set.</summary>
public sealed record DropTableCommand(Guid ConnectionId, string ConnectionName, string Table, bool BackupFirst, string Actor);

/// <summary>
/// Drop (design spec §11.5). Destructive, audited "sqlite.table.drop" on "&lt;connection&gt;/&lt;table&gt;".
/// With BackupFirst it runs Back up now (RunBackupCommandHandler, its own audit row and history
/// record) first, and a failed backup aborts: nothing is dropped and the drop's audit row records
/// why. The page has already confirmed (typed for prod).
/// </summary>
public sealed class DropTableCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    RunBackupCommandHandler backup,
    ILogger<DropTableCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.table.drop";

    public async Task<PluginResult> HandleAsync(DropTableCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Table}";
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {target}", ct);
        if (secret is null)
        {
            return PluginResult.Fail(secretError!);
        }

        string? backupFile = null;
        if (cmd.BackupFirst)
        {
            var backed = await backup.HandleAsync(
                new RunBackupCommand(cmd.ConnectionId, cmd.ConnectionName, cmd.Actor, Note: $"before dropping {cmd.Table}"), ct);
            if (!backed.IsSuccess)
            {
                await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Destructive, succeeded: false,
                    $"backup failed — nothing dropped · {backed.Error}");
                return PluginResult.Fail($"The backup failed, so nothing was dropped: {backed.Error}");
            }

            backupFile = backed.Value!.FileName;
        }

        try
        {
            await operations.DropTableAsync(secret, cmd.Table, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, target);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Destructive, succeeded: false, friendly);
            return PluginResult.Fail(friendly);
        }

        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, target, ActionRisk.Destructive, succeeded: true,
            backupFile is null ? "no backup first" : $"after backup {backupFile}");
        return PluginResult.Ok();
    }
}
