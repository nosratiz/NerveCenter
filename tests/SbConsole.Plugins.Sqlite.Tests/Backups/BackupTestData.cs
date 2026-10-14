using SbConsole.Plugins.Sqlite.Backups;

namespace SbConsole.Plugins.Sqlite.Tests.Backups;

internal static class BackupTestData
{
    public static DateTimeOffset Utc(int year, int month, int day, int hour = 2, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    public static BackupRecord Ok(DateTimeOffset takenAt, string trigger = BackupRecord.TriggerSchedule, string? fileName = null) =>
        new(Guid.NewGuid(), fileName ?? $"reports-{takenAt:yyyyMMdd-HHmmss}.db", takenAt, 1000, 50, trigger, "system", null, BackupRecord.StatusOk, null);

    public static BackupRecord Failed(DateTimeOffset takenAt, string trigger = BackupRecord.TriggerSchedule, string error = "The disk or volume is full.") =>
        new(Guid.NewGuid(), null, takenAt, 0, 10, trigger, "system", null, BackupRecord.StatusFailed, error);
}
