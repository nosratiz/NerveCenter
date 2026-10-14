namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// A table whose foreign key references the table about to be dropped (design spec §11.5): its
/// from-columns and ON DELETE action, and what that action does given that this console's
/// connections run with foreign_keys = ON (DROP TABLE runs an implicit DELETE first).
/// </summary>
public sealed record DropReference(string Table, IReadOnlyList<string> FromColumns, string OnDelete)
{
    public string Consequence => OnDelete.ToUpperInvariant() switch
    {
        "CASCADE" => "rows there are deleted too",
        "SET NULL" or "SET DEFAULT" => "their reference is cleared",
        _ => "the drop fails if any rows reference it", // NO ACTION, RESTRICT
    };
}

/// <summary>The Drop table dialog (mockup 1f): rows ≈ from sqlite_stat1 (null = unknown), index count, referencing tables.</summary>
public sealed record DropImpact(string Table, long? RowsApprox, int IndexCount, IReadOnlyList<DropReference> References);

/// <summary>One object the salvage couldn't copy (Type: table, index, view or trigger), with the friendly reason.</summary>
public sealed record SalvageFailure(string Type, string Name, string Reason);

/// <summary>
/// "Recover into new file" (design spec §11.6): a best-effort copy written to FileName inside the
/// backup directory. RowsSkipped counts rows that failed to insert; a table that couldn't be
/// created, or whose rows stopped reading part-way, is in Failures (rows read before that are kept).
/// OtherObjectsCopied = indexes, views and triggers recreated.
/// </summary>
public sealed record SalvageOutcome(
    string FileName,
    int TablesCopied,
    long RowsCopied,
    long RowsSkipped,
    int OtherObjectsCopied,
    IReadOnlyList<SalvageFailure> Failures,
    TimeSpan Duration)
{
    public int TablesFailed => Failures.Count(f => f.Type == "table");
}
