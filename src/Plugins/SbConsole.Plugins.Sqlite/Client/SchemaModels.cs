namespace SbConsole.Plugins.Sqlite.Client;

public enum SchemaObjectType
{
    Table,
    View,
    Index,
    Trigger,
}

/// <summary>One sqlite_schema row (internal sqlite_* objects excluded).</summary>
internal sealed record SchemaRow(SchemaObjectType Type, string Name, string TableName, string? Sql);

/// <summary>Bytes is null when dbstat isn't available; EstimatedRows is null without a sqlite_stat1 row.</summary>
public sealed record TableSizeEntry(string Name, SchemaObjectType Type, long? Bytes, long? EstimatedRows);

/// <summary>
/// Overview's "Size by table" panel. With dbstat: tables and indexes by bytes. Without it (the
/// bundled build, spec §5): tables only, by sqlite_stat1 rows ≈, unanalyzed tables last.
/// </summary>
public sealed record TableSizes(bool SizesAvailable, int TableCount, int IndexCount, IReadOnlyList<TableSizeEntry> Entries);

public sealed record SchemaObject(string Name, SchemaObjectType Type, string TableName, long? EstimatedRows, long? Bytes);

/// <summary>Tables page left list (mockup 1b). Estimates come from sqlite_stat1, sizes from dbstat when present.</summary>
public sealed record SchemaListing(IReadOnlyList<SchemaObject> Objects, bool SizesAvailable)
{
    public IEnumerable<SchemaObject> Tables => Objects.Where(o => o.Type == SchemaObjectType.Table);

    public IEnumerable<SchemaObject> Views => Objects.Where(o => o.Type == SchemaObjectType.View);

    public int Count(SchemaObjectType type) => Objects.Count(o => o.Type == type);
}

/// <summary>pragma_table_xinfo row. Hidden = virtual-table hidden column (not in SELECT *); Generated = generated column.</summary>
public sealed record ColumnInfo(
    string Name, string DeclaredType, bool NotNull, string? DefaultValue, int PrimaryKeyOrdinal, bool Hidden, bool Generated);

/// <summary>pragma_index_list + pragma_index_info. Origin: "c" (CREATE INDEX), "u" (UNIQUE), "pk".</summary>
public sealed record IndexInfo(string Name, bool Unique, string Origin, bool Partial, IReadOnlyList<string> Columns);

public sealed record ForeignKeyInfo(int Id, string FromColumn, string ToTable, string? ToColumn, string OnUpdate, string OnDelete);

public sealed record TableDetail(
    string Name,
    SchemaObjectType Type,
    string? Ddl,
    bool WithoutRowId,
    long? EstimatedRows,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys)
{
    public bool HasRowId => Type == SchemaObjectType.Table && !WithoutRowId;
}
