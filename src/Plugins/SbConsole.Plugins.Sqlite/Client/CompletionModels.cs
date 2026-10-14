namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>One column for the Query editor's completion (design spec §12): name, declared type ("" when none), part of the primary key.</summary>
public sealed record CompletionColumn(string Name, string DeclaredType, bool PrimaryKey);

/// <summary>A table or view with its columns (pragma_table_xinfo order, at most CompletionSchema.MaxColumns).</summary>
public sealed record CompletionObject(string Name, SchemaObjectType Type, IReadOnlyList<CompletionColumn> Columns);

/// <summary>
/// The tables and views the completion engine knows about (design spec §12), sorted by name. Capped
/// is set when the file had more than MaxObjects of them or any had more than MaxColumns columns.
/// </summary>
public sealed record CompletionSchema(IReadOnlyList<CompletionObject> Objects, bool Capped)
{
    public const int MaxObjects = 500;
    public const int MaxColumns = 200;

    public static CompletionSchema Empty { get; } = new([], false);
}
