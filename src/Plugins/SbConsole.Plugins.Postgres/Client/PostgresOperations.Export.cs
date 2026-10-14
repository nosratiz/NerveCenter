using System.Text;
using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    public Task<CsvExport> ExportTableCsvAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            var (detail, columns) = await RequireRelationAsync(conn, tx, request.Oid, ct);
            var (sql, parameters) = PgRowsSql.BuildExport(PgIdent.Qualified(detail.Schema, detail.Name), columns, request, CsvExport.EffectiveMaxRows);

            await using var cmd = Command(conn, tx, sql, parameters);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var (content, count, truncated) = await WriteCsvAsync(reader, CsvExport.EffectiveMaxRows, CsvExport.MaxBytes, null, ct);
            return new CsvExport($"{SafeFileName(detail.Schema)}.{SafeFileName(detail.Name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv",
                content, count, truncated);
        }, ct);

    /// <summary>Header + up to maxRows rows; truncated when a row was left out by either cap. Shared with the query export, which passes <paramref name="cancel"/> to stop the server streaming the rest.</summary>
    internal static async Task<(byte[] Content, int RowCount, bool Truncated)> WriteCsvAsync(
        NpgsqlDataReader reader, int maxRows, long maxBytes, Action? cancel, CancellationToken ct)
    {
        maxRows = Math.Max(0, maxRows);
        var encoding = new UTF8Encoding(false);
        await using var stream = new MemoryStream();
        await using var writer = new StreamWriter(stream, encoding, 4096, leaveOpen: true);
        var header = CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)) + "\r\n";
        await writer.WriteAsync(header);
        long bytes = encoding.GetByteCount(header);

        var count = 0;
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (count == maxRows)
            {
                truncated = true;
                break;
            }

            var line = CsvFormat.Row(Enumerable.Range(0, reader.FieldCount).Select(i => PgCellFormatter.RawText(reader, i))) + "\r\n";
            bytes += encoding.GetByteCount(line);
            if (bytes > maxBytes)
            {
                truncated = true;
                break;
            }

            await writer.WriteAsync(line);
            count++;
            if (count % 1000 == 0)
            {
                await writer.FlushAsync(ct);
            }
        }

        await writer.FlushAsync(ct);
        if (truncated)
        {
            cancel?.Invoke();
        }

        return (stream.ToArray(), count, truncated);
    }

    private static string SafeFileName(string name) =>
        new(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
}
