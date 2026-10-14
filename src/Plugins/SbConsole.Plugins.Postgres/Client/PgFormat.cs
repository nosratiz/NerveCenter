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
        (double Divisor, string Suffix)[] steps = [(1e3, "K"), (1e6, "M"), (1e9, "B")];
        var abs = Math.Abs((double)value);
        var index = -1;
        for (var k = 0; k < steps.Length; k++)
        {
            if (abs >= steps[k].Divisor)
            {
                index = k;
            }
        }

        if (index < 0)
        {
            return value.ToString(Inv);
        }

        var rounded = RoundCompact(value / steps[index].Divisor);
        if (Math.Abs(rounded) >= 1000 && index < steps.Length - 1)
        {
            index++;
            rounded = RoundCompact(value / steps[index].Divisor);
        }

        return rounded.ToString(Math.Abs(rounded) < 10 ? "0.#" : "0", Inv) + steps[index].Suffix;
    }

    private static double RoundCompact(double scaled) =>
        Math.Abs(scaled) < 10 ? Math.Round(scaled, 1, MidpointRounding.AwayFromZero)
                              : Math.Round(scaled, 0, MidpointRounding.AwayFromZero);

    /// <summary>"0.8 s", "41 s", "14m 02s", "3h 12m", "41d". Negative spans format as their absolute value.</summary>
    public static string Duration(TimeSpan span)
    {
        var seconds = Math.Abs(span.TotalSeconds);
        if (seconds < 9.95)
        {
            return $"{seconds.ToString("0.0", Inv)} s";
        }

        var total = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (total < 60)
        {
            return $"{total} s";
        }

        if (total < 3600)
        {
            return $"{total / 60}m {total % 60:00}s";
        }

        if (total < 86400)
        {
            return $"{total / 3600}h {total % 3600 / 60:00}m";
        }

        return $"{total / 86400}d";
    }

    public static string Ago(TimeSpan span) =>
        span < TimeSpan.FromSeconds(1) ? "just now" : Duration(span) + " ago";

    public static string Percent(double value, int decimals = 1) =>
        value.ToString("F" + decimals, Inv) + "%";
}
