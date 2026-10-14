using System.Text;
using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    public Task<CsvExport> ExportTableCsvAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            var (detail, columns) = await RequireRelationAsync(conn, tx, request.Oid, ct);
            var (sql, parameters) = PgRowsSql.BuildExport(PgIdent.Qualified(detail.Schema, detail.Name), columns, request);

            await using var cmd = Command(conn, tx, sql, parameters);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var (content, count, truncated) = await WriteCsvAsync(reader, CsvExport.EffectiveMaxRows, CsvExport.MaxBytes, ct);
            return new CsvExport($"{SafeFileName(detail.Schema)}.{SafeFileName(detail.Name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv",
                content, count, truncated);
        }, ct);

    /// <summary>Header + up to maxRows rows; truncated when a row was left out by either cap. Shared with the query export.</summary>
    internal static async Task<(byte[] Content, int RowCount, bool Truncated)> WriteCsvAsync(
        NpgsqlDataReader reader, int maxRows, long maxBytes, CancellationToken ct)
    {
        maxRows = Math.Max(0, maxRows);
        await using var stream = new MemoryStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        await writer.WriteAsync(CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        await writer.WriteAsync("\r\n");
        await writer.FlushAsync(ct);

        var count = 0;
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (count == maxRows)
            {
                truncated = true;
                break;
            }

            var before = stream.Length;
            await writer.WriteAsync(CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(i => PgCellFormatter.RawText(reader, i))));
            await writer.WriteAsync("\r\n");
            await writer.FlushAsync(ct);
            if (stream.Length > maxBytes)
            {
                stream.SetLength(before);
                truncated = true;
                break;
            }

            count++;
        }

        return (stream.ToArray(), count, truncated);
    }

    private static string SafeFileName(string name) =>
        new(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
}
