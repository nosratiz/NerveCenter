namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// Grandfather-father-son retention (design spec §11.7), pure so it is tested on its own and the
/// page can show "next prune removes N". Over successful backups only: the newest per UTC day for
/// the last KeepDaily days (today included), the newest per ISO week (Monday-based) for the last
/// KeepWeekly weeks, the newest per month for the last KeepMonthly months, and always the newest
/// successful backup. A manual backup younger than KeepDaily days is exempt -- never pruned, and it
/// doesn't take a bucket's slot from a scheduled backup (a "before migration" copy taken minutes
/// before the nightly run must survive that run's prune); older manual backups go through the
/// normal rules. Failed records older than KeepDaily days are dropped too.
/// </summary>
public static class BackupRetention
{
    /// <summary>The records a prune would delete (files and history), newest first.</summary>
    public static IReadOnlyList<BackupRecord> Plan(IReadOnlyList<BackupRecord> records, BackupPolicy policy, DateTimeOffset nowUtc)
    {
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        var successful = records.Where(r => r.Succeeded).OrderByDescending(r => r.TakenAt).ToList();
        var keep = new HashSet<Guid>();
        if (successful.Count > 0)
        {
            keep.Add(successful[0].Id);
        }

        var dailyCutoff = nowUtc.AddDays(-policy.KeepDaily);
        var exempt = successful.Where(r => r.Trigger == BackupRecord.TriggerManual && r.TakenAt > dailyCutoff).ToList();
        keep.UnionWith(exempt.Select(r => r.Id));
        var bucketed = successful.Except(exempt).ToList();

        KeepNewestPer(bucketed, keep, Day, day => day > today.AddDays(-policy.KeepDaily));
        var thisWeek = WeekStart(today);
        KeepNewestPer(bucketed, keep, r => WeekStart(Day(r)), week => week > thisWeek.AddDays(-7 * policy.KeepWeekly));
        var thisMonth = MonthIndex(today);
        KeepNewestPer(bucketed, keep, r => MonthIndex(Day(r)), month => month > thisMonth - policy.KeepMonthly);

        return records
            .Where(r => r.Succeeded ? !keep.Contains(r.Id) : r.TakenAt < dailyCutoff)
            .OrderByDescending(r => r.TakenAt)
            .ToList();
    }

    /// <summary><paramref name="newestFirst"/> is sorted newest first, so the first record seen per bucket is that bucket's newest.</summary>
    private static void KeepNewestPer<TKey>(List<BackupRecord> newestFirst, HashSet<Guid> keep, Func<BackupRecord, TKey> bucket, Func<TKey, bool> inWindow)
        where TKey : notnull
    {
        var seen = new HashSet<TKey>();
        foreach (var record in newestFirst)
        {
            var key = bucket(record);
            if (inWindow(key) && seen.Add(key))
            {
                keep.Add(record.Id);
            }
        }
    }

    private static DateOnly Day(BackupRecord record) => DateOnly.FromDateTime(record.TakenAt.UtcDateTime);

    /// <summary>The Monday an ISO week starts on -- one bucket per ISO week, year boundaries included.</summary>
    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static int MonthIndex(DateOnly day) => (day.Year * 12) + day.Month - 1;
}
