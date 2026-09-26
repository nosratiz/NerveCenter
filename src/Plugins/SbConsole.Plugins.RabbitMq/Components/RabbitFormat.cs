using System.Globalization;

namespace SbConsole.Plugins.RabbitMq.Components;

/// <summary>
/// Display formatting shared by every RabbitMQ page (design spec §7): invariant culture, counts
/// N0, rates N0 + "/s" (one decimal under 10), null rates as "—" (never 0 -- a null rate means the
/// broker has not sampled yet), byte sizes 1024-based in the management UI's compact style.
/// </summary>
public static class RabbitFormat
{
    public const string Missing = "—";

    private static readonly string[] ByteUnits = ["B", "K", "M", "G", "T", "P"];

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Rate(double? value)
    {
        if (value is not { } rate)
        {
            return Missing;
        }

        if (rate == 0)
        {
            return "0/s";
        }

        var text = Math.Abs(rate) < 10
            ? Math.Round(rate, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(rate, 0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture);
        return text + "/s";
    }

    public static string Bytes(long value)
    {
        if (value < 1024)
        {
            return value.ToString(CultureInfo.InvariantCulture) + "B";
        }

        double scaled = value;
        var unit = 0;
        while (scaled >= 1024 && unit < ByteUnits.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        var text = scaled < 10
            ? scaled.ToString("0.0", CultureInfo.InvariantCulture)
            : Math.Round(scaled, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        return text + ByteUnits[unit];
    }

    public static string Ago(DateTimeOffset then, DateTimeOffset now) => Duration(now - then) + " ago";

    /// <summary>The largest whole unit: "4s", "3m", "2h", "14d". Negative spans clamp to "0s".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h"
            : $"{(int)span.TotalDays}d";
    }

    public static string VhostLabel(string? vhost) =>
        string.IsNullOrEmpty(vhost) || vhost == "/" ? "/ (default)" : vhost;
}
