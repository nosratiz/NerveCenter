namespace SbConsole.Plugins.Postgres.Client;

public enum PgObjectKind { Table, View, MaterializedView, Function, PartitionedTable, ForeignTable }
public sealed record PgObjectNode(uint Oid, string Schema, string Name, PgObjectKind Kind, long? RowEstimate);
public sealed record PgSchemaNode(string Name, IReadOnlyList<PgObjectNode> Tables, IReadOnlyList<PgObjectNode> Views,
    IReadOnlyList<PgObjectNode> MaterializedViews, IReadOnlyList<PgObjectNode> Functions);
public sealed record PgSchemaTree(string Database, IReadOnlyList<PgSchemaNode> Schemas);
public sealed record PgColumn(int Ordinal, string Name, string Type, bool Nullable, string? Default,
    bool IsPrimaryKey, string? References, string? Comment, string? BaseType = null, bool IsGeneratedStored = false, char Identity = '\0');
public sealed record PgIndex(uint Oid, string Name, string Definition, string ColumnsText, string Method, long Bytes,
    long Scans, long TuplesRead, bool IsPrimary, bool IsUnique, bool IsPartial, bool IsValid, bool BacksConstraint);
public sealed record PgConstraint(string Name, string Type, string Definition);   // Type: "primary key" | "foreign key" | "unique" | "check" | "exclusion"
public sealed record PgObjectDetail(uint Oid, string Schema, string Name, PgObjectKind Kind, long? RowEstimate,
    long HeapBytes, long IndexBytes, string Owner, IReadOnlyList<PgColumn> Columns, IReadOnlyList<PgIndex> Indexes,
    IReadOnlyList<PgConstraint> Constraints, DateTimeOffset? StatsResetAt);
