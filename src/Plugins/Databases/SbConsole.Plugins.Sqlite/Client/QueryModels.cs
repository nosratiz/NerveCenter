namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// A result column. DeclaredType is the column's declared type when it comes straight from a
/// table column (null for expressions). UnixMsHint: every non-NULL value read is an INTEGER in the
/// column-notes unix-ms range (ColumnSample.LooksLikeUnixMs) -- a hint only, values never change.
/// </summary>
public sealed record QueryColumn(string Name, string? DeclaredType, bool UnixMsHint);

/// <summary>
/// Query page (mockup 1c, design spec §11.3): the last statement of the script that returned
/// columns. Rows holds at most the requested limit, rendered like the Tables preview; TotalRows
/// keeps counting (without materialising) up to CountCap, TotalCapped when there were more.
/// ResultStatement is that statement's 1-based number, null when no statement returned columns.
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<CellValue>> Rows,
    long TotalRows,
    bool TotalCapped,
    int StatementCount,
    int? ResultStatement,
    TimeSpan Duration)
{
    public const int DefaultLimit = 100;
    public const int CountCap = 100_000;

    public bool HasResultSet => Columns.Count > 0;
}

/// <summary>One EXPLAIN QUERY PLAN row; the page renders the tree from Id/Parent.</summary>
public sealed record ExplainNode(int Id, int Parent, string Detail);

/// <summary>EXPLAIN QUERY PLAN of the script's first statement only (Statement, without any EXPLAIN prefix).</summary>
public sealed record ExplainPlan(string Statement, int StatementCount, IReadOnlyList<ExplainNode> Nodes);

/// <summary>
/// One statement of a write script: DirectRows = changes() for INSERT/UPDATE/DELETE (0 for
/// anything else), CascadedRows = the rest of the total_changes() delta (triggers, FK actions).
/// </summary>
public sealed record WriteStatementResult(string Keyword, string Text, long DirectRows, long CascadedRows);

/// <summary>The file right after a committed write: -wal size (null without one) and free pages.</summary>
public sealed record WriteFileState(long? WalBytes, double FreePagePercent);

/// <summary>
/// A dry run (always rolled back, After null) or a committed write script (design spec §11.4).
/// After is also null when the post-commit read failed -- the commit itself still happened.
/// </summary>
public sealed record WriteScriptResult(bool Committed, IReadOnlyList<WriteStatementResult> Statements, TimeSpan Duration, WriteFileState? After)
{
    public long DirectRows => Statements.Sum(s => s.DirectRows);

    public long CascadedRows => Statements.Sum(s => s.CascadedRows);

    public long TotalRows => DirectRows + CascadedRows;
}

/// <summary>PRAGMA wal_checkpoint(TRUNCATE)'s three columns; LogFrames/CheckpointedFrames are -1 when the file isn't in WAL mode.</summary>
public sealed record WalCheckpointResult(bool Busy, long LogFrames, long CheckpointedFrames);
