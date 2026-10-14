namespace SbConsole.Plugins.Postgres.Query;

/// <summary>One run in the per-connection query history. Hash is QueryAudit.Hash of the full submitted text (links to the audit row).</summary>
public sealed record QueryHistoryEntry(
    string Sql, bool Write, int? Rows, double ElapsedSeconds, string? SqlState, string Actor, DateTimeOffset At, string Hash, string? Database);
