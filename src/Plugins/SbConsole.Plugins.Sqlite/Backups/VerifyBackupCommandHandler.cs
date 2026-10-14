using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

public sealed record VerifyBackupCommand(Guid ConnectionId, string ConnectionName, Guid BackupId);

/// <summary>Record = the history record updated with the outcome; Verification = the full check (null when the file couldn't be read).</summary>
public sealed record VerifyBackupResult(BackupRecord Record, BackupVerification? Verification);

/// <summary>
/// Verify (design spec §11.7). Safe, audited "sqlite.backup.verify". Opens the copy read-only:
/// integrity_check, SHA-256, tables, rows ≈, schema fingerprint -- stored on the history record. A
/// copy with integrity problems is a successful command reporting Verify "failed"; a copy that
/// can't be read fails the command (and is recorded as failed too).
/// </summary>
public sealed class VerifyBackupCommandHandler(
    ISqliteOperations operations,
    IConnectionProvider connections,
    IAuditScope audit,
    [FromKeyedServices(SqlitePlugin.PluginId)] IPluginStore store,
    TimeProvider clock,
    ILogger<VerifyBackupCommandHandler> logger) : IScopedService
{
    public const string Action = "sqlite.backup.verify";

    public async Task<PluginResult<VerifyBackupResult>> HandleAsync(VerifyBackupCommand cmd, CancellationToken ct = default)
    {
        var (secret, secretError) = await HandlerRunner.TryResolveSecretAsync(connections, logger, cmd.ConnectionId, $"{Action} on {cmd.ConnectionName}", ct);
        if (secret is null)
        {
            return PluginResult<VerifyBackupResult>.Fail(secretError!);
        }

        var record = await BackupHandlerSupport.FindAsync(store, cmd.ConnectionId, cmd.BackupId, ct);
        if (record is null)
        {
            return PluginResult<VerifyBackupResult>.Fail(BackupHandlerSupport.BackupNotFound);
        }

        if (record.FileName is null)
        {
            return PluginResult<VerifyBackupResult>.Fail(BackupHandlerSupport.NoFile);
        }

        var (updated, verification, error) = await BackupHandlerSupport.VerifyAsync(operations, secret, record, clock, logger, ct);
        await BackupHandlerSupport.StoreQuietlyAsync(() => BackupHistoryStore.UpdateAsync(store, cmd.ConnectionId, updated, CancellationToken.None),
            logger, "the backup verification", cmd.ConnectionName);

        if (error is not null)
        {
            await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: false, $"{record.FileName} · {error}");
            return PluginResult<VerifyBackupResult>.Fail(error);
        }

        var detail = verification!.IntegrityOk
            ? $"{record.FileName} · ok · sha256:{verification.Sha256[..12]}"
            : $"{record.FileName} · failed · {verification.Messages.Count} {(verification.Messages.Count == 1 ? "problem" : "problems")}";
        await HandlerRunner.AuditQuietlyAsync(audit, logger, Action, cmd.ConnectionName, ActionRisk.Safe, succeeded: true, detail);
        return PluginResult<VerifyBackupResult>.Ok(new VerifyBackupResult(updated, verification));
    }
}
