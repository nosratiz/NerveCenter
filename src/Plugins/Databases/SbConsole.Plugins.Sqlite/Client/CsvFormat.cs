namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>RFC 4180 fields: quote when the value holds a quote, comma, CR or LF; double embedded quotes. Values are exported verbatim.</summary>
public static class CsvFormat
{
    public static string Field(string? value)
    {
        if (value is null)
        {
            return "";
        }

        return value.AsSpan().IndexOfAny("\",\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    public static string Row(IEnumerable<string?> values) => string.Join(",", values.Select(Field));
}
