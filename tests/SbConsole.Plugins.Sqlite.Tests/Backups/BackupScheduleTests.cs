using FluentAssertions;
using SbConsole.Plugins.Sqlite.Backups;
using static SbConsole.Plugins.Sqlite.Tests.Backups.BackupTestData;

namespace SbConsole.Plugins.Sqlite.Tests.Backups;

public class BackupScheduleTests
{
    private static readonly BackupPolicy Daily = new(Enabled: true, DailyAtUtc: "02:00");

    [Fact]
    public void Not_due_without_an_enabled_valid_policy()
    {
        var now = Utc(2026, 10, 13, 12);

        BackupSchedule.IsDue(null, [], now).Should().BeFalse();
        BackupSchedule.IsDue(Daily with { Enabled = false }, [], now).Should().BeFalse();
        BackupSchedule.IsDue(Daily with { DailyAtUtc = "25:00" }, [], now).Should().BeFalse();
    }

    [Fact]
    public void Not_due_before_todays_slot()
    {
        BackupSchedule.IsDue(Daily, [], Utc(2026, 10, 13, 1, 59)).Should().BeFalse();
    }

    [Fact]
    public void Due_once_the_slot_has_passed_with_no_scheduled_record_after_it()
    {
        BackupSchedule.IsDue(Daily, [], Utc(2026, 10, 13, 2, 0)).Should().BeTrue();
        BackupSchedule.IsDue(Daily, [Ok(Utc(2026, 10, 12, 2))], Utc(2026, 10, 13, 9)).Should().BeTrue();
    }

    [Fact]
    public void Not_due_again_after_a_scheduled_run_today_even_a_failed_one()
    {
        var now = Utc(2026, 10, 13, 9);

        BackupSchedule.IsDue(Daily, [Ok(Utc(2026, 10, 13, 2, 1))], now).Should().BeFalse();
        BackupSchedule.IsDue(Daily, [Failed(Utc(2026, 10, 13, 2, 1))], now).Should().BeFalse();
    }

    [Fact]
    public void A_manual_backup_after_the_slot_does_not_replace_the_scheduled_one()
    {
        BackupSchedule.IsDue(Daily, [Ok(Utc(2026, 10, 13, 3), BackupRecord.TriggerManual)], Utc(2026, 10, 13, 9)).Should().BeTrue();
    }

    [Fact]
    public void Next_slot_is_today_before_the_slot_and_tomorrow_after_it()
    {
        BackupSchedule.NextSlot(Daily, Utc(2026, 10, 13, 1)).Should().Be(Utc(2026, 10, 13, 2));
        BackupSchedule.NextSlot(Daily, Utc(2026, 10, 13, 2)).Should().Be(Utc(2026, 10, 14, 2));
        BackupSchedule.NextSlot(null, Utc(2026, 10, 13, 2)).Should().BeNull();
    }

    [Fact]
    public void Every_slot_of_the_last_seven_days_without_a_success_is_missed()
    {
        BackupSchedule.MissedSlots(Daily, [], Utc(2026, 10, 13, 12)).Should().Be(7);
    }

    [Fact]
    public void Slots_with_a_success_of_any_trigger_in_the_next_24_hours_are_not_missed()
    {
        var now = Utc(2026, 10, 13, 12);
        var history = Enumerable.Range(0, 7).Select(i => Ok(Utc(2026, 10, 13, 2, 5).AddDays(-i))).ToList();
        history[3] = Ok(Utc(2026, 10, 10, 20), BackupRecord.TriggerManual);
        history[4] = Failed(Utc(2026, 10, 9, 2, 5));

        BackupSchedule.MissedSlots(Daily, history, now).Should().Be(1);
    }

    [Fact]
    public void A_slot_within_the_grace_hour_is_not_missed_yet()
    {
        var history = Enumerable.Range(1, 6).Select(i => Ok(Utc(2026, 10, 13, 2, 5).AddDays(-i))).ToList();

        BackupSchedule.MissedSlots(Daily, history, Utc(2026, 10, 13, 2, 30)).Should().Be(0);
        BackupSchedule.MissedSlots(Daily, history, Utc(2026, 10, 13, 3, 0)).Should().Be(1);
        BackupSchedule.MissedSlots(null, [], Utc(2026, 10, 13, 3, 0)).Should().Be(0);
    }

    [Fact]
    public void Slots_before_the_policy_was_enabled_are_not_missed()
    {
        var now = Utc(2026, 10, 13, 12);

        BackupSchedule.MissedSlots(Daily with { EnabledSince = Utc(2026, 10, 13, 11) }, [], now).Should().Be(0);
        BackupSchedule.MissedSlots(Daily with { EnabledSince = Utc(2026, 10, 11, 1) }, [], now).Should().Be(3);
        BackupSchedule.MissedSlots(Daily with { EnabledSince = Utc(2026, 10, 11, 2) }, [], now).Should().Be(3);
    }

    [Fact]
    public void An_old_stored_policy_without_enabled_since_counts_all_seven_days()
    {
        BackupSchedule.MissedSlots(Daily with { EnabledSince = null }, [], Utc(2026, 10, 13, 12)).Should().Be(7);
    }

    [Fact]
    public void Hours_since_the_last_success_ignore_failures()
    {
        var now = Utc(2026, 10, 13, 12);
        List<BackupRecord> history = [Failed(Utc(2026, 10, 13, 2)), Ok(Utc(2026, 10, 12, 2)), Ok(Utc(2026, 10, 11, 2))];

        BackupSchedule.HoursSinceLastSuccess(history, now).Should().Be(34);
        BackupSchedule.LastSuccess(history)!.TakenAt.Should().Be(Utc(2026, 10, 12, 2));
        BackupSchedule.HoursSinceLastSuccess([], now).Should().BeNull();
    }

    [Theory]
    [InlineData(25.9, false)]
    [InlineData(26.0, false)]
    [InlineData(26.1, true)]
    public void Overdue_after_26_hours(double hours, bool overdue)
    {
        var now = Utc(2026, 10, 13, 12);

        BackupSchedule.IsOverdue(Daily, [Ok(now.AddHours(-hours))], now).Should().Be(overdue);
    }

    [Fact]
    public void Overdue_with_no_success_only_when_a_policy_is_enabled()
    {
        var now = Utc(2026, 10, 13, 12);

        BackupSchedule.IsOverdue(Daily, [Failed(now)], now).Should().BeTrue();
        BackupSchedule.IsOverdue(Daily with { Enabled = false }, [], now).Should().BeFalse();
        BackupSchedule.IsOverdue(null, [], now).Should().BeFalse();
    }
}
