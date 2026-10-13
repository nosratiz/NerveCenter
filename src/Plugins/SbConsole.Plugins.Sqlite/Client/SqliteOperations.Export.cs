using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<CsvExport> ExportCsvAsync(string secret, string name, int maxRows, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            maxRows = Math.Max(0, maxRows);
            var obj = RequireObject(c, name);
            using var command = c.CreateCommand();
            // One extra row tells "exactly maxRows" apart from "more than maxRows".
            command.CommandText = $"SELECT * FROM {SqliteSql.QuoteIdentifier(obj.Name)} LIMIT {((long)maxRows + 1).ToString(CultureInfo.InvariantCulture)}";
            using var reader = command.ExecuteReader();

            var csv = new StringBuilder();
            csv.Append(CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName))).Append("\r\n");
            var count = 0;
            var truncated = false;
            while (reader.Read())
            {
                if (count == maxRows)
                {
                    truncated = true;
                    break;
                }

                csv.Append(CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(i => CsvValue(reader, i)))).Append("\r\n");
                count++;
            }

            return new CsvExport($"{SafeFileName(obj.Name)}.csv", Encoding.UTF8.GetBytes(csv.ToString()), count, truncated);
        }, ct);

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
