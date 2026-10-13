using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

public sealed record DownloadBackupCommand(Guid ConnectionId, string ConnectionName, Guid BackupId);

/// <summary>
/// Download (design spec §11.7). Safe but audited "sqlite.backup.download" -- the whole database
/// leaves the server. The caller streams BackupFileStream.Content to the browser and disposes it.
/// </summary>
public sealed class DownloadBackupCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    ILogger<DownloadBackupCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.backup.download";

    public async Task<PluginResult<BackupFileStream>> HandleAsync(DownloadBackupCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<BackupFileStream>.Fail(secretError!);
        }

        var record = await BackupHandlerSupport.FindAsync(store, cmd.ConnectionId, cmd.BackupId, ct);
        if (record is null)
        {
            return PluginResult<BackupFileStream>.Fail(BackupHandlerSupport.BackupNotFound);
        }

        if (record.FileName is null)
        {
            return PluginResult<BackupFileStream>.Fail(BackupHandlerSupport.NoFile);
        }

        BackupFileStream download;
        try
        {
            download = await operations.OpenBackupStreamAsync(secret, record.FileName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: false, $"{record.FileName} · {friendly}");
            return PluginResult<BackupFileStream>.Fail(friendly);
        }

        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: true,
            $"{record.FileName} · {SqliteFormat.Bytes(download.Length)}");
        return PluginResult<BackupFileStream>.Ok(download);
    }
}
