namespace SbConsole.Plugins.Postgres.Client;

public enum PgFilterOp { Eq, NotEq, Gt, Gte, Lt, Lte, Like, ILike, IsNull, IsNotNull, Contains }
public sealed record PgFilter(string Column, PgFilterOp Op, string? Value);
public sealed record PgRowsRequest(uint Oid, IReadOnlyList<PgFilter> Filters, string? SortColumn, bool SortDescending, int Offset, int Limit);
public enum PgCellKind { Null, Text, Json, Bytes }
public sealed record PgCell(PgCellKind Kind, string? Display, int? JsonKeyCount = null, string? Full = null);
public sealed record PgResultColumn(string Name, string TypeName);
public sealed record PgRowPage(IReadOnlyList<PgResultColumn> Columns, IReadOnlyList<IReadOnlyList<PgCell>> Rows, long? EstimatedMatching);
