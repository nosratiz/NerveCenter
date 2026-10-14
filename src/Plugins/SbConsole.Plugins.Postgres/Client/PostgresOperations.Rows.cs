using System.Text.Json;
using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    public Task<PgRowPage> PreviewRowsAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            var (detail, columns) = await RequireRelationAsync(conn, tx, request.Oid, ct);
            var qualified = PgIdent.Qualified(detail.Schema, detail.Name);
            var (sql, parameters) = PgRowsSql.Build(qualified, columns, request);

            var resultColumns = new List<PgResultColumn>();
            var rows = new List<IReadOnlyList<PgCell>>();
            await using (var cmd = Command(conn, tx, sql, parameters))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                for (var i = 0; i < r.FieldCount; i++)
                {
                    resultColumns.Add(new PgResultColumn(r.GetName(i), r.GetDataTypeName(i)));
                }

                while (await r.ReadAsync(ct))
                {
                    var row = new PgCell[r.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = PgCellFormatter.Format(r, i);
                    }

                    rows.Add(row);
                }
            }

            var estimate = request.Filters.Count == 0
                ? detail.RowEstimate
                : await EstimateAsync(conn, tx, PgRowsSql.Build(qualified, columns, request, forCount: true), ct);
            return new PgRowPage(resultColumns, rows, estimate);
        }, ct);

    private static async Task<long?> EstimateAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        (string Sql, IReadOnlyList<string?> Parameters) select, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, "EXPLAIN (FORMAT JSON) " + select.Sql, select.Parameters);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement[0].GetProperty("Plan").GetProperty("Plan Rows").GetDouble() is var rows
            ? (long)Math.Round(rows)
            : null;
    }

    /// <summary>The relation's detail plus its (name, type) columns; the browse page may only ask about tables, views and matviews.</summary>
    private static async Task<(PgObjectDetail Detail, IReadOnlyList<(string Name, string Type)> Columns)> RequireRelationAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, uint oid, CancellationToken ct)
    {
        var detail = await ReadDetailAsync(conn, tx, oid, ct);
        if (detail is null || detail.Kind == PgObjectKind.Function)
        {
            throw new PgSqlRefusedException("That object no longer exists, or has no rows to browse. Refresh the tree.");
        }

        return (detail, detail.Columns.Select(c => (c.Name, c.Type)).ToList());
    }

    /// <summary>Unnamed positional parameters ($1, $2 ...); the server infers each type from its ::cast.</summary>
    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, IReadOnlyList<string?> parameters)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var value in parameters)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)value ?? DBNull.Value });
        }

        return cmd;
    }
}
