using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// Actor is the person (from the page's auth state) or "system" for the scheduler; it goes into
/// the history record -- the audit row records its own actor. Trigger: BackupRecord.TriggerManual
/// or TriggerSchedule.
/// </summary>
public sealed record RunBackupCommand(Guid ConnectionId, string ConnectionName, string Actor, string Trigger = BackupRecord.TriggerManual, string? Note = null);

/// <summary>
/// Back up now / the scheduled backup (design spec §11.7). Safe, audited "sqlite.backup.create".
/// The history record is stored even when the backup fails (with the friendly error); a successful
/// backup is then verified and its record updated -- a verify problem shows in the record, it
/// doesn't fail the command.
/// </summary>
public sealed class RunBackupCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<RunBackupCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.backup.create";

    public async Task<PluginResult<BackupRecord>> HandleAsync(RunBackupCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<BackupRecord>.Fail(secretError!);
        }

        var takenAt = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        BackupCreated created;
        try
        {
            created = await operations.CreateBackupAsync(secret, takenAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Action} on {Target} failed.", Action, cmd.ConnectionName);
            var friendly = FriendlySqliteError.From(ex);
            var failed = new BackupRecord(Guid.NewGuid(), null, takenAt, 0, (long)clock.GetElapsedTime(started).TotalMilliseconds,
                cmd.Trigger, cmd.Actor, cmd.Note, BackupRecord.StatusFailed, friendly);
            await BackupHandlerSupport.StoreQuietlyAsync(() => BackupHistoryStore.AddAsync(store, cmd.ConnectionId, failed, CancellationToken.None),
                logger, "the failed backup record", cmd.ConnectionName);
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: false, $"{cmd.Trigger} · {friendly}");
            return PluginResult<BackupRecord>.Fail(friendly);
        }

        var record = new BackupRecord(Guid.NewGuid(), created.FileName, takenAt, created.Bytes, (long)created.Duration.TotalMilliseconds,
            cmd.Trigger, cmd.Actor, cmd.Note, BackupRecord.StatusOk, null);
        await BackupHandlerSupport.StoreQuietlyAsync(() => BackupHistoryStore.AddAsync(store, cmd.ConnectionId, record, CancellationToken.None),
            logger, "the backup record", cmd.ConnectionName);
        var detail = $"{created.FileName} · {SqliteFormat.Bytes(created.Bytes)} · {SqliteFormat.Duration(created.Duration)} · {cmd.Trigger}"
            + (string.IsNullOrWhiteSpace(cmd.Note) ? "" : $" · {cmd.Note.Trim()}");
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: true, detail);

        try
        {
            (record, _, _) = await BackupHandlerSupport.VerifyAsync(operations, secret, record, clock, logger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The backup exists and is recorded; it just stays "unverified".
            return PluginResult<BackupRecord>.Ok(record);
        }

        var verified = record;
        await BackupHandlerSupport.StoreQuietlyAsync(() => BackupHistoryStore.UpdateAsync(store, cmd.ConnectionId, verified, CancellationToken.None),
            logger, "the backup verification", cmd.ConnectionName);
        return PluginResult<BackupRecord>.Ok(record);
    }
}
