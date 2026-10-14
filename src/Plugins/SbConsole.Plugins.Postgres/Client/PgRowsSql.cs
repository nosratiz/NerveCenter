using System.Globalization;
using System.Text;

namespace SbConsole.Plugins.Postgres.Client;

/// <summary>
/// Builds the browse-page SELECT. Identifiers are quoted with <see cref="PgIdent"/>, values are positional parameters
/// ($1, $2 ...) and the <c>::type</c> cast text is the catalog's <c>format_type</c> output for the column -- the caller
/// passes the detail's column list, so no client-supplied text is ever interpolated.
/// </summary>
internal static class PgRowsSql
{
    public const int MaxLimit = 100;

    /// <summary>The page query, or (<paramref name="forCount"/>) the same filters without ORDER BY / LIMIT / OFFSET for EXPLAIN.</summary>
    public static (string Sql, IReadOnlyList<string?> Parameters) Build(string qualifiedName,
        IReadOnlyList<PgRowColumn> columns, PgRowsRequest request, bool forCount = false) =>
        Compose(qualifiedName, columns, request, order: !forCount, page: !forCount);

    /// <summary>The export query: filters and sort, bounded to one row past the cap so truncation is detectable without draining the table.</summary>
    public static (string Sql, IReadOnlyList<string?> Parameters) BuildExport(string qualifiedName,
        IReadOnlyList<PgRowColumn> columns, PgRowsRequest request, int maxRows) =>
        Compose(qualifiedName, columns, request, order: true, page: false, exportLimit: (long)Math.Max(0, maxRows) + 1);

    private static (string Sql, IReadOnlyList<string?> Parameters) Compose(string qualifiedName,
        IReadOnlyList<PgRowColumn> columns, PgRowsRequest request, bool order, bool page, long? exportLimit = null)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            types[column.Name] = column.CastType;
        }

        var parameters = new List<string?>();
        var sql = new StringBuilder("SELECT ").Append(SelectList(columns)).Append(" FROM ").Append(qualifiedName);

        var clauses = new List<string>();
        foreach (var filter in request.Filters)
        {
            if (!types.TryGetValue(filter.Column, out var type))
            {
                throw new PgSqlRefusedException($"There is no column named \"{Clip(filter.Column)}\" in this object.");
            }

            clauses.Add(Clause(filter, type, parameters));
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", clauses));
        }

        if (order && request.SortColumn is { } sort)
        {
            if (!types.ContainsKey(sort))
            {
                throw new PgSqlRefusedException($"There is no column named \"{Clip(sort)}\" to sort by.");
            }

            sql.Append(" ORDER BY ").Append(PgIdent.Quote(sort)).Append(request.SortDescending ? " DESC" : " ASC").Append(" NULLS LAST");
        }

        if (page)
        {
            var limit = Math.Clamp(request.Limit, 1, MaxLimit);
            var offset = Math.Max(0, request.Offset);
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {limit} OFFSET {offset}");
        }
        else if (exportLimit is { } cap)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {cap}");
        }

        return (sql.ToString(), parameters);
    }

    private static readonly HashSet<string> SafeTypes = new(StringComparer.Ordinal)
    {
        "smallint", "integer", "bigint", "real", "double precision", "boolean", "text", "character varying", "character",
        "uuid", "json", "jsonb", "bytea", "name", "\"char\"",
    };

    /// <summary>
    /// Explicit select list. Types Npgsql cannot always materialise (interval with months, NaN / huge numeric, BC dates)
    /// and anything not known to be safe are read as the server's own text form.
    /// </summary>
    private static string SelectList(IReadOnlyList<PgRowColumn> columns)
    {
        if (columns.Count == 0)
        {
            return "*";
        }

        return string.Join(", ", columns.Select(c =>
        {
            var q = PgIdent.Quote(c.Name);
            return SafeTypes.Contains(c.CastType.Replace("[]", "", StringComparison.Ordinal)) ? q : $"{q}::text AS {q}";
        }));
    }

    private static string Clause(PgFilter filter, string type, List<string?> parameters)
    {
        var col = PgIdent.Quote(filter.Column);
        switch (filter.Op)
        {
            case PgFilterOp.IsNull:
                return $"{col} IS NULL";
            case PgFilterOp.IsNotNull:
                return $"{col} IS NOT NULL";
        }

        if (filter.Value is null)
        {
            throw new PgSqlRefusedException($"The filter on \"{Clip(filter.Column)}\" needs a value.");
        }

        if (filter.Op == PgFilterOp.Contains && type != "jsonb" && !type.EndsWith("[]", StringComparison.Ordinal))
        {
            throw new PgSqlRefusedException($"\"contains\" only works on jsonb and array columns; \"{Clip(filter.Column)}\" is {type}.");
        }

        parameters.Add(filter.Value);
        var n = parameters.Count.ToString(CultureInfo.InvariantCulture);
        return filter.Op switch
        {
            PgFilterOp.Eq => $"{col} = ${n}::{type}",
            PgFilterOp.NotEq => $"{col} <> ${n}::{type}",
            PgFilterOp.Gt => $"{col} > ${n}::{type}",
            PgFilterOp.Gte => $"{col} >= ${n}::{type}",
            PgFilterOp.Lt => $"{col} < ${n}::{type}",
            PgFilterOp.Lte => $"{col} <= ${n}::{type}",
            PgFilterOp.Like => $"{col}::text LIKE ${n}::text",
            PgFilterOp.ILike => $"{col}::text ILIKE ${n}::text",
            PgFilterOp.Contains => $"{col} @> ${n}::{type}",
            _ => throw new PgSqlRefusedException("Unsupported filter operator."),
        };
    }

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
}

/// <summary>A browsable column: <see cref="Type"/> is the display text (with typmod), <see cref="BaseType"/> the typmod-free type used for casts.</summary>
internal sealed record PgRowColumn(string Name, string Type, string? BaseType = null)
{
    public string CastType => BaseType ?? Type;
}
