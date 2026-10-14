using FluentAssertions;
using SbConsole.Plugins.Sqlite.Backups;
using static SbConsole.Plugins.Sqlite.Tests.Backups.BackupTestData;

namespace SbConsole.Plugins.Sqlite.Tests.Backups;

public class BackupRetentionTests
{
    private static readonly BackupPolicy Policy = new(Enabled: true, KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 3);

    private static List<DateOnly> KeptDays(IReadOnlyList<BackupRecord> records, IReadOnlyList<BackupRecord> deleted) =>
        records.Except(deleted).Select(r => DateOnly.FromDateTime(r.TakenAt.UtcDateTime)).Order().ToList();

    [Fact]
    public void Nothing_to_prune_in_an_empty_history()
    {
        BackupRetention.Plan([], Policy, Utc(2026, 10, 13, 12)).Should().BeEmpty();
    }

    [Fact]
    public void Daily_backups_keep_seven_days_four_weeks_and_three_months()
    {
        // Tuesday 2026-10-13; one backup a day at 02:00 for 120 days.
        var now = Utc(2026, 10, 13, 12);
        var records = Enumerable.Range(0, 120).Select(i => Ok(Utc(2026, 10, 13).AddDays(-i))).ToList();

        var deleted = BackupRetention.Plan(records, Policy, now);

        KeptDays(records, deleted).Should().Equal(
            new DateOnly(2026, 8, 31),                            // month: August
            new DateOnly(2026, 9, 27),                            // week of Mon 21 Sep
            new DateOnly(2026, 9, 30),                            // month: September
            new DateOnly(2026, 10, 4),                            // week of Mon 28 Sep
            new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8), // the last 7 days (today included),
            new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 10),// which also cover this week, last week
            new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 12),// and October
            new DateOnly(2026, 10, 13));
        deleted.Should().BeInDescendingOrder(r => r.TakenAt);
    }

    [Fact]
    public void Only_the_newest_backup_of_a_day_is_kept()
    {
        var now = Utc(2026, 10, 13, 23);
        var early = Ok(Utc(2026, 10, 13, 2), BackupRecord.TriggerSchedule);
        var late = Ok(Utc(2026, 10, 13, 15), BackupRecord.TriggerSchedule);

        BackupRetention.Plan([late, early], Policy, now).Should().Equal(early);
    }

    [Fact]
    public void A_manual_backup_minutes_before_a_scheduled_one_the_same_day_is_not_pruned()
    {
        // Observed: manual "before retention delete" at 12:37, the scheduler's backup at 12:39, then its prune.
        var now = Utc(2026, 10, 13, 12, 40);
        var manual = Ok(Utc(2026, 10, 13, 12, 37), BackupRecord.TriggerManual);
        var scheduled = Ok(Utc(2026, 10, 13, 12, 39), BackupRecord.TriggerSchedule);

        BackupRetention.Plan([scheduled, manual], Policy, now).Should().BeEmpty();
    }

    [Fact]
    public void A_recent_manual_backup_does_not_take_the_days_slot_from_a_scheduled_one()
    {
        var now = Utc(2026, 10, 13, 23);
        var scheduled = Ok(Utc(2026, 10, 13, 2), BackupRecord.TriggerSchedule);
        var manual = Ok(Utc(2026, 10, 13, 15), BackupRecord.TriggerManual);
        var olderScheduled = Ok(Utc(2026, 10, 13, 1), BackupRecord.TriggerSchedule);

        BackupRetention.Plan([manual, scheduled, olderScheduled], Policy, now).Should().Equal(olderScheduled);
    }

    [Fact]
    public void A_manual_backup_older_than_the_daily_window_goes_through_the_normal_rules()
    {
        // KeepDaily 7: Oct 5 is outside the window; the week of Mon 5 Oct keeps its newest, the Oct 6 scheduled one.
        var now = Utc(2026, 10, 13, 12);
        var recent = Ok(Utc(2026, 10, 13));
        var oldManual = Ok(Utc(2026, 10, 5, 10), BackupRecord.TriggerManual);
        var weekNewest = Ok(Utc(2026, 10, 6));

        BackupRetention.Plan([recent, weekNewest, oldManual], Policy with { KeepMonthly = 0 }, now).Should().Equal(oldManual);
    }

    [Fact]
    public void The_newest_successful_backup_is_always_kept()
    {
        var now = Utc(2026, 10, 13, 12);
        var old = Ok(Utc(2026, 1, 1));
        var older = Ok(Utc(2025, 12, 1));

        var deleted = BackupRetention.Plan([old, older], new BackupPolicy(Enabled: true, KeepDaily: 1, KeepWeekly: 0, KeepMonthly: 0), now);

        deleted.Should().Equal(older);
    }

    [Fact]
    public void Failed_records_older_than_the_daily_window_are_dropped_and_recent_ones_kept()
    {
        var now = Utc(2026, 10, 13, 12);
        var ok = Ok(Utc(2026, 10, 13));
        var recentFailure = Failed(Utc(2026, 10, 10));
        var oldFailure = Failed(Utc(2026, 10, 1));

        BackupRetention.Plan([ok, recentFailure, oldFailure], Policy, now).Should().Equal(oldFailure);
    }

    [Fact]
    public void A_backup_whose_verify_failed_still_counts_as_a_successful_backup()
    {
        var now = Utc(2026, 10, 13, 12);
        var newest = Ok(Utc(2026, 10, 13)) with { Verify = BackupRecord.VerifyFailed };
        var older = Ok(Utc(2026, 10, 13, 1));

        BackupRetention.Plan([newest, older], Policy, now).Should().Equal(older);
    }

    [Fact]
    public void An_iso_week_spanning_new_year_is_one_bucket()
    {
        // Saturday 2027-01-02. Week of Mon 28 Dec 2026 holds Dec 31 and Jan 1; Dec 27 is the week before.
        var now = Utc(2027, 1, 2, 12);
        var dec27 = Ok(Utc(2026, 12, 27));
        var dec31 = Ok(Utc(2026, 12, 31));
        var jan1 = Ok(Utc(2027, 1, 1));

        var deleted = BackupRetention.Plan([jan1, dec31, dec27], new BackupPolicy(Enabled: true, KeepDaily: 1, KeepWeekly: 2, KeepMonthly: 0), now);

        deleted.Should().Equal(dec31);
    }

    [Fact]
    public void Monthly_buckets_keep_the_newest_of_each_month()
    {
        var now = Utc(2026, 10, 13, 12);
        var oct = Ok(Utc(2026, 10, 2));
        var sepLate = Ok(Utc(2026, 9, 20));
        var sepEarly = Ok(Utc(2026, 9, 3));
        var aug = Ok(Utc(2026, 8, 3));
        var jul = Ok(Utc(2026, 7, 3));

        var deleted = BackupRetention.Plan([oct, sepLate, sepEarly, aug, jul], new BackupPolicy(Enabled: true, KeepDaily: 1, KeepWeekly: 0, KeepMonthly: 3), now);

        deleted.Should().Equal(sepEarly, jul);
    }
}
