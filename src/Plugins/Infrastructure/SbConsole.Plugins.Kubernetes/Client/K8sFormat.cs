using System.Globalization;
using System.Text.RegularExpressions;

namespace SbConsole.Plugins.Kubernetes.Client;

public static partial class K8sFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Age(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        if (t.TotalSeconds < 60) return $"{(long)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(long)t.TotalMinutes}m";
        if (t.TotalHours < 24) return $"{(long)t.TotalHours}h";
        var days = (long)t.TotalDays;
        return days >= 365 * 2 ? $"{days / 365}y" : $"{days}d";
    }

    public static string Ago(TimeSpan t) => t.TotalSeconds < 5 ? "just now" : Age(t) + " ago";

    public static string Cpu(long milli) =>
        milli < 1000 ? $"{milli}m" : (Math.Round(milli / 1000.0, 1, MidpointRounding.AwayFromZero)).ToString("0.#", Inv);

    public static string Bytes(long b)
    {
        if (b < 1024) return $"{b} B";
        string[] units = ["Ki", "Mi", "Gi", "Ti"];
        double v = b;
        var i = -1;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        var rounded = Math.Round(v, 1, MidpointRounding.AwayFromZero);
        var text = v < 10 && v != Math.Floor(v)
            ? rounded.ToString("0.#", Inv)
            : Math.Round(v, 0, MidpointRounding.AwayFromZero).ToString("0", Inv);
        return $"{text} {units[i]}";
    }

    public static string Percent(double fraction) =>
        Math.Round(fraction * 100, 0, MidpointRounding.AwayFromZero).ToString("0", Inv) + "%";

    public static string Images(IReadOnlyList<string> images) => string.Join(" · ", images);

    public static long ParseCpuMilli(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity)) return 0;
        var q = quantity.Trim();
        if (q.EndsWith('m'))
        {
            return (long)Math.Round(ParseNumber(q[..^1]), MidpointRounding.AwayFromZero);
        }

        return (long)Math.Round(ParseNumber(q) * 1000, MidpointRounding.AwayFromZero);
    }

    public static long ParseBytes(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity)) return 0;
        var q = quantity.Trim();
        if (ExponentPattern().IsMatch(q))
        {
            return (long)Math.Round(ParseNumber(q), MidpointRounding.AwayFromZero);
        }

        var m = QuantityPattern().Match(q);
        if (!m.Success)
        {
            throw new FormatException($"Unrecognised quantity '{q}'.");
        }

        var multiplier = m.Groups[2].Value switch
        {
            "" => 1d,
            "Ki" => Math.Pow(1024, 1), "Mi" => Math.Pow(1024, 2), "Gi" => Math.Pow(1024, 3),
            "Ti" => Math.Pow(1024, 4), "Pi" => Math.Pow(1024, 5), "Ei" => Math.Pow(1024, 6),
            "k" => 1e3, "M" => 1e6, "G" => 1e9, "T" => 1e12, "P" => 1e15, "E" => 1e18,
            _ => throw new FormatException($"Unrecognised quantity '{q}'."),
        };
        return (long)Math.Round(ParseNumber(m.Groups[1].Value) * multiplier, MidpointRounding.AwayFromZero);
    }

    private static double ParseNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : throw new FormatException($"Unrecognised quantity '{s}'.");

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)?[eE][+-]?[0-9]+$")]
    private static partial Regex ExponentPattern();

    [GeneratedRegex(@"^([0-9]*\.?[0-9]+)([A-Za-z]*)$")]
    private static partial Regex QuantityPattern();
}
