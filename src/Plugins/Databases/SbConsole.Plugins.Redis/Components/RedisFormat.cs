using System.Globalization;

namespace SbConsole.Plugins.Redis.Components;

/// <summary>Number/size/duration formatting shared by every Redis page, matching the mockup's copy ("38 KB", "6.9 GB", "2.41M", "2h 4m", "0.4s").</summary>
public static class RedisFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Binary (1024) scaling, matching Redis's own INFO convention. B and KB are whole numbers; MB and up keep one decimal.</summary>
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

        return unit <= 1
            ? $"{Math.Round(value).ToString("0", Inv)} {units[unit]}"
            : $"{value.ToString("0.0", Inv)} {units[unit]}";
    }

    public static string Count(long value) => value.ToString("N0", Inv);

    /// <summary>Full digits below 10k; three significant digits with a k/M/B suffix above.</summary>
    public static string Compact(long value)
    {
        if (Math.Abs(value) < 10_000)
        {
            return Count(value);
        }

        (double scaled, string suffix) = Math.Abs(value) switch
        {
            >= 1_000_000_000 => (value / 1_000_000_000d, "B"),
            >= 1_000_000 => (value / 1_000_000d, "M"),
            _ => (value / 1_000d, "k"),
        };
        var format = Math.Abs(scaled) >= 100 ? "0" : Math.Abs(scaled) >= 10 ? "0.#" : "0.##";
        return scaled.ToString(format, Inv) + suffix;
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 10)
        {
            return span.TotalSeconds.ToString("0.#", Inv) + "s";
        }

        if (span.TotalMinutes < 1)
        {
            return ((int)span.TotalSeconds).ToString(Inv) + "s";
        }

        if (span.TotalHours < 1)
        {
            return ((int)span.TotalMinutes).ToString(Inv) + "m";
        }

        if (span.TotalDays < 1)
        {
            return span.Minutes == 0
                ? $"{(int)span.TotalHours}h"
                : $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return span.Hours == 0 ? $"{(int)span.TotalDays}d" : $"{(int)span.TotalDays}d {span.Hours}h";
    }

    public static string Ago(DateTimeOffset at, DateTimeOffset now) => Duration(now - at) + " ago";

    /// <summary>A stream entry id is "{unix-ms}-{seq}"; the prefix is when it was added.</summary>
    public static DateTimeOffset? StreamIdTime(string id)
    {
        var dash = id.IndexOf('-', StringComparison.Ordinal);
        var prefix = dash < 0 ? id : id[..dash];
        return long.TryParse(prefix, NumberStyles.None, Inv, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
    }
}
