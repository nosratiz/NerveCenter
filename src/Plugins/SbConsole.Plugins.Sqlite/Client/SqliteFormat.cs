using System.Globalization;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Number/size/duration copy shared by every SQLite page, matching the mockup ("2.1 GB", "48 MB", "41.2M", "2m 14s").</summary>
public static class SqliteFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Binary (1024) units; one decimal below 10 (KB and up), whole numbers otherwise.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit > 0 && value < 10 ? "0.#" : "0";
        return $"{value.ToString(format, Inv)} {units[unit]}";
    }

    public static string Count(long value) => value.ToString("N0", Inv);

    /// <summary>For ≈ row estimates: "41.2M" from a million up, grouped digits below.</summary>
    public static string Approx(long rows) =>
        rows >= 1_000_000 ? $"{(rows / 1_000_000d).ToString("0.0", Inv)}M" : Count(rows);

    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
        {
            return $"{(int)duration.TotalMilliseconds} ms";
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            return $"{duration.TotalSeconds.ToString("0.#", Inv)} s";
        }

        return $"{(int)duration.TotalMinutes}m {duration.Seconds.ToString("00", Inv)}s";
    }

    /// <summary>How long ago, for the backup tiles: "12 min", "31h" (under two days), "3d".</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min";
        }

        return age < TimeSpan.FromHours(48) ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalDays}d";
    }

    /// <summary>A size delta: "+310 MB", "−260 MB" (U+2212), "0 B".</summary>
    public static string SignedBytes(long delta) => delta switch
    {
        > 0 => "+" + Bytes(delta),
        < 0 => "\u2212" + Bytes(delta == long.MinValue ? long.MaxValue : -delta),
        _ => Bytes(0),
    };

    /// <summary>A rows ≈ delta: "+1.2M", "−4,118", "0".</summary>
    public static string SignedApprox(long delta) => delta switch
    {
        > 0 => "+" + Approx(delta),
        < 0 => "\u2212" + Approx(delta == long.MinValue ? long.MaxValue : -delta),
        _ => "0",
    };
}
