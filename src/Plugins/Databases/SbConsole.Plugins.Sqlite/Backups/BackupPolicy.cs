using System.Globalization;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// A connection's backup schedule and retention (design spec §11.7): one backup a day at
/// DailyAtUtc ("HH:mm", UTC), grandfather-father-son retention over KeepDaily days, KeepWeekly ISO
/// weeks and KeepMonthly months. Disabled by default. EnabledSince is when the schedule was last
/// switched on (set by the save handler, null while disabled and in policies stored before it
/// existed) so "missed N" doesn't count slots from before then.
/// </summary>
public sealed record BackupPolicy(
    bool Enabled = false,
    string DailyAtUtc = BackupPolicy.DefaultDailyAtUtc,
    int KeepDaily = 7,
    int KeepWeekly = 4,
    int KeepMonthly = 3,
    DateTimeOffset? EnabledSince = null)
{
    public const string DefaultDailyAtUtc = "02:00";

    public static BackupPolicy Default { get; } = new();

    /// <summary>Null when valid, else the first problem in words.</summary>
    public string? Validate()
    {
        if (!TryParseDailyAt(DailyAtUtc, out _))
        {
            return "Daily time must be HH:mm (UTC), e.g. 02:00.";
        }

        if (KeepDaily < 1)
        {
            return "Keep at least 1 daily backup.";
        }

        return KeepWeekly < 0 || KeepMonthly < 0 ? "Weekly and monthly counts can't be negative." : null;
    }

    /// <summary>DailyAtUtc as a time of day; only valid policies should be asked.</summary>
    public TimeOnly DailyAt => TryParseDailyAt(DailyAtUtc, out var time) ? time : throw new FormatException(Validate());

    public static bool TryParseDailyAt(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>"daily 02:00 · 7d · 4w · 3m" or "disabled", for audit details and the Retention tile.</summary>
    public string Describe() => Enabled
        ? string.Create(CultureInfo.InvariantCulture, $"daily {DailyAtUtc} UTC · {KeepDaily}d · {KeepWeekly}w · {KeepMonthly}m")
        : string.Create(CultureInfo.InvariantCulture, $"disabled · {KeepDaily}d · {KeepWeekly}w · {KeepMonthly}m");
}
