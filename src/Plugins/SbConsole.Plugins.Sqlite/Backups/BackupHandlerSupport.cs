using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>Shared by the backup handlers and the scheduler: verify-and-record, history lookups and quiet store writes.</summary>
internal static class BackupHandlerSupport
{
    public const string BackupNotFound = "Backup not found — it may have been pruned.";
    public const string NoFile = "That backup has no file — it failed before anything was written.";

    /// <summary>
    /// Verifies the record's file and returns the record updated with the outcome. An integrity
    /// failure is a result (Verify "failed", first message as detail); an error opening/reading the
    /// file is too, plus Error for the caller. A cancellation the caller asked for is rethrown.
    /// </summary>
    public static async Task<(BackupRecord Record, BackupVerification? Verification, string? Error)> VerifyAsync(
        ISqliteOperations operations, string secret, BackupRecord record, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            var verification = await operations.VerifyBackupAsync(secret, record.FileName!, ct);
            var detail = verification.IntegrityOk
                ? null
                : verification.Messages.Count == 1 ? verification.Messages[0] : $"{verification.Messages[0]} (+{verification.Messages.Count - 1} more)";
            return (record with
            {
                Verify = verification.IntegrityOk ? BackupRecord.VerifyOk : BackupRecord.VerifyFailed,
                VerifyDetail = detail,
                Sha256 = verification.Sha256,
                Tables = verification.File.Tables,
                RowsApprox = verification.File.RowsApprox,
                SchemaFingerprint = verification.File.SchemaFingerprint,
                VerifiedAt = clock.GetUtcNow(),
            }, verification, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Verifying backup {File} failed.", record.FileName);
            var friendly = FriendlySqliteError.From(ex);
            return (record with { Verify = BackupRecord.VerifyFailed, VerifyDetail = friendly, VerifiedAt = clock.GetUtcNow() }, null, friendly);
        }
    }

    public static async Task<BackupRecord?> FindAsync(IPluginStore store, Guid connectionId, Guid backupId, CancellationToken ct) =>
        (await BackupHistoryStore.ReadAsync(store, connectionId, ct)).FirstOrDefault(r => r.Id == backupId);

    /// <summary>History writes after the work happened: a failure is logged, never thrown (the file on disk is what matters).</summary>
    public static async Task StoreQuietlyAsync(Func<Task> write, ILogger logger, string what, string target)
    {
        try
        {
            await write();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Storing {What} for {Target} failed.", what, target);
        }
    }
}
