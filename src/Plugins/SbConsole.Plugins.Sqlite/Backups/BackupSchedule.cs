namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// When a scheduled backup is due, and how far behind a connection is (design spec §11.7). Pure:
/// the scheduler, the Backups page tiles, the dashboard problems and the nav badge all ask this.
/// </summary>
public static class BackupSchedule
{
    /// <summary>"Backup overdue" once the newest successful backup is older than this (a day plus slack).</summary>
    public const double OverdueHours = 26;

    /// <summary>A slot this recent isn't "missed" yet -- the backup may still be running.</summary>
    public static readonly TimeSpan MissedGrace = TimeSpan.FromHours(1);

    public const int MissedSlotDays = 7;

    /// <summary>Today's (UTC) slot at the policy's DailyAtUtc.</summary>
    public static DateTimeOffset TodaysSlot(BackupPolicy policy, DateTimeOffset nowUtc) =>
        new(DateOnly.FromDateTime(nowUtc.UtcDateTime).ToDateTime(policy.DailyAt), TimeSpan.Zero);

    /// <summary>
    /// Enabled and valid, today's slot has passed, and no scheduled record (ok or failed -- a failure
    /// is not retried every minute) was taken at or after it.
    /// </summary>
    public static bool IsDue(BackupPolicy? policy, IReadOnlyList<BackupRecord> history, DateTimeOffset nowUtc)
    {
        if (!IsActive(policy))
        {
            return false;
        }

        var slot = TodaysSlot(policy!, nowUtc);
        return nowUtc >= slot && !history.Any(r => r.Trigger == BackupRecord.TriggerSchedule && r.TakenAt >= slot);
    }

    /// <summary>The next slot strictly after now; null without an active policy.</summary>
    public static DateTimeOffset? NextSlot(BackupPolicy? policy, DateTimeOffset nowUtc)
    {
        if (!IsActive(policy))
        {
            return null;
        }

        var slot = TodaysSlot(policy!, nowUtc);
        return slot > nowUtc ? slot : slot.AddDays(1);
    }

    /// <summary>
    /// Slots in the last MissedSlotDays days (at least MissedGrace old) with no successful backup --
    /// of any trigger -- in the 24 hours from the slot. 0 without an active policy.
    /// </summary>
    public static int MissedSlots(BackupPolicy? policy, IReadOnlyList<BackupRecord> history, DateTimeOffset nowUtc)
    {
        if (!IsActive(policy))
        {
            return 0;
        }

        var today = TodaysSlot(policy!, nowUtc);
        var missed = 0;
        for (var i = 0; i < MissedSlotDays; i++)
        {
            var slot = today.AddDays(-i);
            if (slot + MissedGrace > nowUtc)
            {
                continue;
            }

            if (!history.Any(r => r.Succeeded && r.TakenAt >= slot && r.TakenAt < slot.AddDays(1)))
            {
                missed++;
            }
        }

        return missed;
    }

    public static BackupRecord? LastSuccess(IReadOnlyList<BackupRecord> history) =>
        history.Where(r => r.Succeeded).MaxBy(r => r.TakenAt);

    public static double? HoursSinceLastSuccess(IReadOnlyList<BackupRecord> history, DateTimeOffset nowUtc) =>
        LastSuccess(history) is { } last ? (nowUtc - last.TakenAt).TotalHours : null;

    /// <summary>Active policy and no successful backup at all, or the newest is older than OverdueHours.</summary>
    public static bool IsOverdue(BackupPolicy? policy, IReadOnlyList<BackupRecord> history, DateTimeOffset nowUtc) =>
        IsActive(policy) && (HoursSinceLastSuccess(history, nowUtc) ?? double.PositiveInfinity) > OverdueHours;

    private static bool IsActive(BackupPolicy? policy) => policy is { Enabled: true } && policy.Validate() is null;
}
