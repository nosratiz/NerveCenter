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
