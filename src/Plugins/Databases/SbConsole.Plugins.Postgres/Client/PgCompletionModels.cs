namespace SbConsole.Plugins.Postgres.Client;

/// <summary>One column for the Query editor's completion (design spec §16): name, format_type() text, part of the primary key.</summary>
public sealed record PgCompletionColumn(string Name, string Type, bool PrimaryKey);

/// <summary>A table, view, materialized view, partitioned or foreign table with its columns (attnum order, at most MaxColumns).</summary>
public sealed record PgCompletionObject(string Schema, string Name, PgObjectKind Kind, IReadOnlyList<PgCompletionColumn> Columns);

/// <summary>
/// What the completion engine knows about one database (design spec §16): the session's search path
/// (current_schemas(false)), the non-system schemas and up to MaxObjects relations, ordered by schema, name.
/// </summary>
public sealed record PgCompletionSchema(IReadOnlyList<string> SearchPath, IReadOnlyList<string> Schemas, IReadOnlyList<PgCompletionObject> Objects)
{
    public const int MaxObjects = 2000;
    public const int MaxColumns = 200;

    public static PgCompletionSchema Empty { get; } = new([], [], []);
}
