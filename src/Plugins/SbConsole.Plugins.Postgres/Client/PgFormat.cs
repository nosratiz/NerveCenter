using System.Globalization;

namespace SbConsole.Plugins.Postgres.Client;

public static class PgFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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

    /// <summary>Tree-badge form: 999, 88K, 2.1M, 410M, 1.1B.</summary>
    public static string Compact(long value)
    {
        (double Divisor, string Suffix)[] steps = [(1e9, "B"), (1e6, "M"), (1e3, "K")];
        foreach (var (divisor, suffix) in steps)
        {
            if (Math.Abs(value) >= divisor)
            {
                var scaled = value / divisor;
                return scaled.ToString(scaled < 10 ? "0.#" : "0", Inv) + suffix;
            }
        }

        return value.ToString(Inv);
    }

    /// <summary>"0.8 s", "41 s", "14m 02s", "3h 12m", "41d".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 10)
        {
            return $"{span.TotalSeconds.ToString("0.0", Inv)} s";
        }

        if (span.TotalMinutes < 1)
        {
            return $"{(int)span.TotalSeconds} s";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes}m {span.Seconds:00}s";
        }

        if (span.TotalDays < 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        }

        return $"{(int)span.TotalDays}d";
    }

    public static string Ago(TimeSpan span) =>
        span < TimeSpan.FromSeconds(1) ? "just now" : Duration(span) + " ago";

    public static string Percent(double value, int decimals = 1) =>
        value.ToString("F" + decimals, Inv) + "%";
}
