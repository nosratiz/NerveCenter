using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

public sealed partial class SqliteOperations
{
    public Task<SchemaListing> ListSchemaAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var rows = ReadStat1Rows(c);
            var sizes = TryReadDbstatSizes(c);
            var objects = ReadSchemaObjects(c)
                .Select(o => new SchemaObject(
                    o.Name,
                    o.Type,
                    o.TableName,
                    o.Type == SchemaObjectType.Table ? Lookup(rows, o.Name) : null,
                    sizes is null ? null : Lookup(sizes, o.Name)))
                .ToList();
            return new SchemaListing(objects, sizes is not null, new FileInfo(c.DataSource).Length);
        }, ct);

    public Task<TableDetail> GetTableDetailAsync(string secret, string name, CancellationToken ct = default) =>
        RunAsync(secret, c => ReadDetail(c, name), ct);

    /// <summary>
    /// The gate for every per-table query: the name must be a table or view in sqlite_schema before
    /// it is ever quoted into SQL.
    /// </summary>
    internal static SchemaRow RequireObject(SqliteConnection c, string name) =>
        SqliteSql.Rows(c, "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name = $name AND type IN ('table', 'view')",
                ReadSchemaRow, ("$name", name))
            .FirstOrDefault()
        ?? throw new SqliteObjectNotFoundException(name);

    internal static TableDetail ReadDetail(SqliteConnection c, string name)
    {
        var obj = RequireObject(c, name);
        var isTable = obj.Type == SchemaObjectType.Table;

        var columns = SqliteSql.Rows(c,
            "SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo($name)",
            r => new ColumnInfo(
                r.GetString(0),
                r.GetString(1),
                r.GetInt64(2) != 0,
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4),
                Hidden: r.GetInt64(5) == 1,
                Generated: r.GetInt64(5) is 2 or 3),
            ("$name", obj.Name));

        IReadOnlyList<IndexInfo> indexes = isTable ? ReadIndexes(c, obj.Name) : [];

        IReadOnlyList<ForeignKeyInfo> foreignKeys = isTable
            ? SqliteSql.Rows(c,
                "SELECT id, \"from\", \"table\", \"to\", on_update, on_delete FROM pragma_foreign_key_list($name) ORDER BY id, seq",
                r => new ForeignKeyInfo(r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5)),
                ("$name", obj.Name))
            : [];

        var withoutRowId = isTable
            && SqliteSql.Long(c, "SELECT wr FROM pragma_table_list WHERE schema = 'main' AND name = $name", ("$name", obj.Name)) != 0;

        return new TableDetail(
            obj.Name, obj.Type, obj.Sql, withoutRowId,
            isTable ? Lookup(ReadStat1Rows(c), obj.Name) : null,
            columns, indexes, foreignKeys);
    }

    private static List<IndexInfo> ReadIndexes(SqliteConnection c, string table) =>
        SqliteSql.Rows(c, "SELECT name, \"unique\", origin, partial FROM pragma_index_list($name) ORDER BY name",
                r => (Name: r.GetString(0), Unique: r.GetInt64(1) != 0, Origin: r.GetString(2), Partial: r.GetInt64(3) != 0),
                ("$name", table))
            .Select(i => new IndexInfo(i.Name, i.Unique, i.Origin, i.Partial,
                SqliteSql.Rows(c, "SELECT name FROM pragma_index_info($index) ORDER BY seqno",
                    r => r.IsDBNull(0) ? "(expression)" : r.GetString(0), ("$index", i.Name))))
            .ToList();
}
