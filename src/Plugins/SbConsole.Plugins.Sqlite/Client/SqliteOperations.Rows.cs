using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    internal const int MaxCellText = 200;

    public Task<RowPage> PreviewRowsAsync(string secret, string name, int limit, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var detail = ReadDetail(c, name);
            var sql = $"SELECT * FROM {SqliteSql.QuoteIdentifier(detail.Name)}{Order(detail, descending: true)} LIMIT {limit.ToString(CultureInfo.InvariantCulture)}";
            using var command = c.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<IReadOnlyList<CellValue>>();
            while (reader.Read())
            {
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => ReadCell(reader, i)).ToList());
            }

            return new RowPage(sql, columns, rows);
        }, ct);

    public Task<long> CountRowsAsync(string secret, string name, CancellationToken ct = default) =>
        RunAsync(secret, c => SqliteSql.Long(c, $"SELECT COUNT(*) FROM {SqliteSql.QuoteIdentifier(RequireObject(c, name).Name)}"), ct);

    public Task<IReadOnlyList<ColumnSample>> SampleColumnsAsync(string secret, string name, int sampleRows, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<ColumnSample>>(secret, c =>
        {
            var detail = ReadDetail(c, name);
            var source = $"SELECT * FROM {SqliteSql.QuoteIdentifier(detail.Name)}{Order(detail, descending: false)} LIMIT {sampleRows.ToString(CultureInfo.InvariantCulture)}";
            var min = ColumnSample.EpochMsMin.ToString(CultureInfo.InvariantCulture);
            var max = ColumnSample.EpochMsMax.ToString(CultureInfo.InvariantCulture);

            return detail.Columns.Where(col => !col.Hidden).Select(col =>
            {
                var q = SqliteSql.QuoteIdentifier(col.Name);
                var sql = $$"""
                    WITH s AS ({{source}})
                    SELECT COUNT(*), COUNT({{q}}), COUNT(DISTINCT {{q}}),
                           COALESCE(SUM(typeof({{q}}) = 'text'), 0),
                           COALESCE(SUM(typeof({{q}}) = 'text' AND substr(ltrim({{q}}), 1, 1) IN ('{', '[') AND json_valid({{q}})), 0),
                           COALESCE(SUM(typeof({{q}}) = 'integer'), 0),
                           COALESCE(SUM(typeof({{q}}) = 'integer' AND {{q}} BETWEEN {{min}} AND {{max}}), 0)
                    FROM s
                    """;
                return SqliteSql.Rows(c, sql, r => new ColumnSample(
                    col.Name, r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)))[0];
            }).ToList();
        }, ct);

    /// <summary>
    /// rowid order for rowid tables (newest first when descending), primary-key order for WITHOUT
    /// ROWID tables, none for views. A column literally named rowid shadows it, so fall back to the
    /// other two aliases SQLite accepts.
    /// </summary>
    private static string Order(TableDetail detail, bool descending)
    {
        var direction = descending ? " DESC" : "";
        if (detail.HasRowId)
        {
            var names = detail.Columns.Select(col => col.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var alias = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(a => !names.Contains(a));
            return alias is null ? "" : $" ORDER BY {alias}{direction}";
        }

        var pk = detail.Columns.Where(col => col.PrimaryKeyOrdinal > 0).OrderBy(col => col.PrimaryKeyOrdinal).ToList();
        return detail.Type == SchemaObjectType.Table && pk.Count > 0
            ? " ORDER BY " + string.Join(", ", pk.Select(col => SqliteSql.QuoteIdentifier(col.Name) + direction))
            : "";
    }

    internal static CellValue ReadCell(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return new CellValue(CellKind.Null, "NULL");
        }

        return reader.GetValue(ordinal) switch
        {
            long integer => new CellValue(CellKind.Integer, integer.ToString(CultureInfo.InvariantCulture), integer),
            double real => new CellValue(CellKind.Real, real.ToString("R", CultureInfo.InvariantCulture)),
            byte[] blob => new CellValue(CellKind.Blob, $"BLOB · {SqliteFormat.Count(blob.Length)} bytes"),
            var other => Text(Convert.ToString(other, CultureInfo.InvariantCulture) ?? ""),
        };

        static CellValue Text(string text) => text.Length <= MaxCellText
            ? new CellValue(CellKind.Text, text)
            : new CellValue(CellKind.Text, text[..MaxCellText] + "…", Truncated: true);
    }
}
