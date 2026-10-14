using System.Globalization;

namespace SbConsole.Plugins.Postgres.Client;

internal static class PgFormat
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
}
