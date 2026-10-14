using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

/// <summary>Turns one reader value into a <see cref="PgCell"/> for the grid, or into raw text for CSV.</summary>
internal static class PgCellFormatter
{
    public const int DisplayChars = 200;
    public const int FullChars = 10_000;
    private const int ByteSample = 16;

    public static PgCell Format(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal))
        {
            return new PgCell(PgCellKind.Null, null);
        }

        var type = r.GetDataTypeName(ordinal);
        if (type is "json" or "jsonb")
        {
            return FormatJson(r.GetString(ordinal));
        }

        if (type == "bytea")
        {
            var bytes = r.GetFieldValue<byte[]>(ordinal);
            var sample = bytes.Length > ByteSample ? bytes.AsSpan(0, ByteSample) : bytes;
            var shown = "\\x" + Convert.ToHexStringLower(sample) + (bytes.Length > ByteSample ? "… (" + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes)" : "");
            return new PgCell(PgCellKind.Bytes, shown);
        }

        var text = RawText(r, ordinal) ?? "";
        return text.Length <= DisplayChars
            ? new PgCell(PgCellKind.Text, text)
            : new PgCell(PgCellKind.Text, text[..DisplayChars] + "…", Full: Cap(text));
    }

    /// <summary>The full, untruncated text of a non-null value (CSV and the "Full" side of a cell).</summary>
    public static string? RawText(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal))
        {
            return null;
        }

        var type = r.GetDataTypeName(ordinal);
        if (type is "json" or "jsonb")
        {
            return r.GetString(ordinal);
        }

        if (type == "bytea")
        {
            return "\\x" + Convert.ToHexStringLower(r.GetFieldValue<byte[]>(ordinal));
        }

        var value = r.GetValue(ordinal);
        if (type == "date" && value is DateTime d)
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return Text(value);
    }

    private static PgCell FormatJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var pretty = Cap(JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    var keys = root.EnumerateObject().Count();
                    return new PgCell(PgCellKind.Json, $"{{…}} {keys} {(keys == 1 ? "key" : "keys")}", keys, pretty);
                case JsonValueKind.Array:
                    var items = root.GetArrayLength();
                    return new PgCell(PgCellKind.Json, $"[…] {items} {(items == 1 ? "item" : "items")}", items, pretty);
                default:
                    var scalar = root.ToString();
                    return new PgCell(PgCellKind.Json,
                        scalar.Length <= DisplayChars ? scalar : scalar[..DisplayChars] + "…", null, pretty);
            }
        }
        catch (JsonException)
        {
            return new PgCell(PgCellKind.Text, json.Length <= DisplayChars ? json : json[..DisplayChars] + "…", Full: Cap(json));
        }
    }

    private static string Cap(string s) => s.Length <= FullChars ? s : s[..FullChars];

    private static string Text(object? value) => value switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        byte[] bytes => "\\x" + Convert.ToHexStringLower(bytes),
        Array array => ArrayText(array),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>{a,b,c}; nested arrays become nested braces. Elements are not re-quoted -- it is a display form.</summary>
    private static string ArrayText(Array array)
    {
        var sb = new StringBuilder();
        Append(array, [], sb);
        return sb.ToString();
    }

    private static void Append(Array array, int[] prefix, StringBuilder sb)
    {
        sb.Append('{');
        var dim = prefix.Length;
        var length = array.GetLength(dim);
        var lower = array.GetLowerBound(dim);
        for (var i = 0; i < length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var index = prefix.Append(lower + i).ToArray();
            if (dim + 1 < array.Rank)
            {
                Append(array, index, sb);
            }
            else
            {
                var element = array.GetValue(index);
                sb.Append(element is null or DBNull ? "NULL" : Text(element));
            }
        }

        sb.Append('}');
    }
}
