namespace SbConsole.Plugins.Postgres.Client;

public sealed record PgQueryRequest(string Sql, bool Write, int RowCap, int TimeoutS);
public sealed record PgQueryResult(IReadOnlyList<PgResultColumn> Columns, IReadOnlyList<IReadOnlyList<PgCell>> Rows,
    bool Capped, long? RowsAffected, TimeSpan Elapsed, int StatementCount, string? CommandTag);
