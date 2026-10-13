using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<SqliteOverview> GetOverviewAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var path = c.DataSource;
            var file = new FileInfo(path);
            var wal = new FileInfo(path + "-wal");
            return new SqliteOverview(
                path,
                SqliteSql.Text(c, "SELECT sqlite_version()"),
                file.Length,
                wal.Exists ? wal.Length : null,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                SqliteSql.Long(c, "PRAGMA page_size"),
                SqliteSql.Long(c, "PRAGMA page_count"),
                SqliteSql.Long(c, "PRAGMA freelist_count"),
                SqliteSql.Text(c, "PRAGMA journal_mode").ToUpperInvariant(),
                SynchronousName(SqliteSql.Long(c, "PRAGMA synchronous")),
                SqliteSql.Long(c, "PRAGMA wal_autocheckpoint"),
                SqliteSql.Long(c, "PRAGMA cache_size"),
                (int)SqliteSql.Long(c, "PRAGMA busy_timeout"));
        }, ct);

    public Task<TableSizes> GetTableSizesAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var objects = ReadSchemaObjects(c);
            var rows = ReadStat1Rows(c);
            var sizes = TryReadDbstatSizes(c);

            var entries = objects
                .Where(o => o.Type is SchemaObjectType.Table or SchemaObjectType.Index)
                .Select(o => new TableSizeEntry(
                    o.Name,
                    o.Type,
                    sizes is null ? null : Lookup(sizes, o.Name),
                    o.Type == SchemaObjectType.Table ? Lookup(rows, o.Name) : null))
                .ToList();

            IReadOnlyList<TableSizeEntry> ordered = sizes is not null
                ? entries.OrderByDescending(e => e.Bytes ?? 0).ThenBy(e => e.Name, StringComparer.Ordinal).ToList()
                : entries.Where(e => e.Type == SchemaObjectType.Table)
                    .OrderByDescending(e => e.EstimatedRows.HasValue)
                    .ThenByDescending(e => e.EstimatedRows ?? 0)
                    .ThenBy(e => e.Name, StringComparer.Ordinal)
                    .ToList();

            return new TableSizes(
                sizes is not null,
                objects.Count(o => o.Type == SchemaObjectType.Table),
                objects.Count(o => o.Type == SchemaObjectType.Index),
                ordered);
        }, ct);

    internal static string SynchronousName(long level) => level switch
    {
        0 => "OFF",
        1 => "NORMAL",
        2 => "FULL",
        3 => "EXTRA",
        _ => level.ToString(CultureInfo.InvariantCulture),
    };

    internal static List<SchemaRow> ReadSchemaObjects(SqliteConnection c) =>
        SqliteSql.Rows(c,
            @"SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\' ORDER BY name",
            ReadSchemaRow);

    internal static SchemaRow ReadSchemaRow(SqliteDataReader r) =>
        new(ParseType(r.GetString(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));

    private static SchemaObjectType ParseType(string type) => type switch
    {
        "table" => SchemaObjectType.Table,
        "view" => SchemaObjectType.View,
        "index" => SchemaObjectType.Index,
        "trigger" => SchemaObjectType.Trigger,
        _ => throw new InvalidOperationException($"Unknown sqlite_schema type '{type}'."),
    };

    /// <summary>
    /// Row estimates per table from sqlite_stat1: the first integer of `stat` is the table's row
    /// count (on every index row and on the idx-NULL row of an index-less table). Missing table =
    /// never analyzed = no estimate.
    /// </summary>
    internal static Dictionary<string, long> ReadStat1Rows(SqliteConnection c)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (SqliteSql.Long(c, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'sqlite_stat1'") == 0)
        {
            return result;
        }

        foreach (var (table, stat) in SqliteSql.Rows(c, "SELECT tbl, stat FROM sqlite_stat1",
                     r => (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1))))
        {
            var first = stat.Split(' ', 2)[0];
            if (long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var rows))
            {
                result[table] = Math.Max(result.GetValueOrDefault(table), rows);
            }
        }

        return result;
    }

    /// <summary>Bytes per table/index from the dbstat virtual table, or null when this SQLite build lacks it.</summary>
    internal static Dictionary<string, long>? TryReadDbstatSizes(SqliteConnection c)
    {
        try
        {
            return SqliteSql.Rows(c, "SELECT name, SUM(pgsize) FROM dbstat GROUP BY name", r => (Name: r.GetString(0), Bytes: r.GetInt64(1)))
                .ToDictionary(x => x.Name, x => x.Bytes, StringComparer.Ordinal);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table: dbstat", StringComparison.Ordinal))
        {
            return null;
        }
    }

    internal static long? Lookup(Dictionary<string, long> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;
}
