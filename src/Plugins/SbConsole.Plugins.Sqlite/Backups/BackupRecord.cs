namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// One row of a connection's backup history (design spec §11.7), stored even when the backup
/// failed. FileName is the bare name inside BackupDir, null when no file was written. Status:
/// "ok" | "failed" (Error holds the friendly message). Verify: "ok" | "failed" | "unverified";
/// VerifyDetail is the first integrity message or the verify error.
/// </summary>
public sealed record BackupRecord(
    Guid Id,
    string? FileName,
    DateTimeOffset TakenAt,
    long Bytes,
    long DurationMs,
    string Trigger,
    string Actor,
    string? Note,
    string Status,
    string? Error,
    string Verify = BackupRecord.VerifyUnverified,
    string? VerifyDetail = null,
    string? Sha256 = null,
    long? Tables = null,
    long? RowsApprox = null,
    string? SchemaFingerprint = null,
    DateTimeOffset? VerifiedAt = null)
{
    public const string TriggerManual = "manual";
    public const string TriggerSchedule = "schedule";
    public const string StatusOk = "ok";
    public const string StatusFailed = "failed";
    public const string VerifyOk = "ok";
    public const string VerifyFailed = "failed";
    public const string VerifyUnverified = "unverified";

    public bool Succeeded => Status == StatusOk;
}
