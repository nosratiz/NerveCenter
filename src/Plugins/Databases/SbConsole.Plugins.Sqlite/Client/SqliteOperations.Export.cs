using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<CsvExport> ExportCsvAsync(string secret, string name, int maxRows, long maxBytes, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            maxRows = Math.Max(0, maxRows);
            var obj = RequireObject(c, name);
            using var command = c.CreateCommand();
            // One extra row tells "exactly maxRows" apart from "more than maxRows".
            command.CommandText = $"SELECT * FROM {SqliteSql.QuoteIdentifier(obj.Name)} LIMIT {((long)maxRows + 1).ToString(CultureInfo.InvariantCulture)}";
            using var reader = command.ExecuteReader();
            var (content, count, truncated) = WriteCsv(reader, maxRows, maxBytes);
            return new CsvExport($"{SafeFileName(obj.Name)}.csv", content, count, truncated);
        }, ct);

    /// <summary>Header + up to maxRows rows of <paramref name="reader"/>; truncated when a row was left out by either cap.</summary>
    internal static (byte[] Content, int RowCount, bool Truncated) WriteCsv(SqliteDataReader reader, int maxRows, long maxBytes)
    {
        maxRows = Math.Max(0, maxRows);

        // One buffer: rows go straight into the MemoryStream (UTF-8, no BOM) instead of StringBuilder -> string -> byte[].
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        writer.Write("\r\n");
        writer.Flush();
        var count = 0;
        var truncated = false;
        while (reader.Read())
        {
            if (count == maxRows)
            {
                truncated = true;
                break;
            }

            var before = stream.Length;
            writer.Write(CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(i => CsvValue(reader, i))));
            writer.Write("\r\n");
            writer.Flush();
            if (stream.Length > maxBytes)
            {
                // This row would push past the cap: drop it.
                stream.SetLength(before);
                truncated = true;
                break;
            }

            count++;
        }

        return (stream.ToArray(), count, truncated);
    }

    private static string? CsvValue(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : reader.GetValue(ordinal) switch
        {
            byte[] blob => Convert.ToBase64String(blob),
            double real => real.ToString("R", CultureInfo.InvariantCulture),
            var other => Convert.ToString(other, CultureInfo.InvariantCulture),
        };

    internal static string SafeFileName(string name) =>
        new(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
}
