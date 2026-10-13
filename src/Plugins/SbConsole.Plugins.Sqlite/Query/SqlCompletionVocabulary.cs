namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>
/// SQLite's keyword set (https://sqlite.org/lang_keywords.html, 147 words) and its built-in
/// functions -- core, aggregate, window, date/time, math and JSON (design spec §12). Operators such
/// as -> and ->> are not functions and are not listed.
/// </summary>
internal static class SqlCompletionVocabulary
{
    public static readonly string[] Keywords =
    [
        "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "ANALYZE", "AND", "AS", "ASC", "ATTACH", "AUTOINCREMENT",
        "BEFORE", "BEGIN", "BETWEEN", "BY", "CASCADE", "CASE", "CAST", "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONFLICT",
        "CONSTRAINT", "CREATE", "CROSS", "CURRENT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE", "DEFAULT",
        "DEFERRABLE", "DEFERRED", "DELETE", "DESC", "DETACH", "DISTINCT", "DO", "DROP", "EACH", "ELSE", "END", "ESCAPE", "EXCEPT",
        "EXCLUDE", "EXCLUSIVE", "EXISTS", "EXPLAIN", "FAIL", "FILTER", "FIRST", "FOLLOWING", "FOR", "FOREIGN", "FROM", "FULL",
        "GENERATED", "GLOB", "GROUP", "GROUPS", "HAVING", "IF", "IGNORE", "IMMEDIATE", "IN", "INDEX", "INDEXED", "INITIALLY",
        "INNER", "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LAST", "LEFT", "LIKE", "LIMIT", "MATCH",
        "MATERIALIZED", "NATURAL", "NO", "NOT", "NOTHING", "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON", "OR", "ORDER",
        "OTHERS", "OUTER", "OVER", "PARTITION", "PLAN", "PRAGMA", "PRECEDING", "PRIMARY", "QUERY", "RAISE", "RANGE", "RECURSIVE",
        "REFERENCES", "REGEXP", "REINDEX", "RELEASE", "RENAME", "REPLACE", "RESTRICT", "RETURNING", "RIGHT", "ROLLBACK", "ROW",
        "ROWS", "SAVEPOINT", "SELECT", "SET", "TABLE", "TEMP", "TEMPORARY", "THEN", "TIES", "TO", "TRANSACTION", "TRIGGER",
        "UNBOUNDED", "UNION", "UNIQUE", "UPDATE", "USING", "VACUUM", "VALUES", "VIEW", "VIRTUAL", "WHEN", "WHERE", "WINDOW",
        "WITH", "WITHOUT",
    ];

    public static readonly HashSet<string> KeywordSet = new(Keywords, StringComparer.OrdinalIgnoreCase);

    /// <summary>Name and signature; a signature ending in "()" takes no arguments.</summary>
    public static readonly (string Name, string Signature)[] Functions =
    [
        // core
        ("abs", "abs(X)"), ("changes", "changes()"), ("char", "char(X1, X2, …)"), ("coalesce", "coalesce(X, Y, …)"),
        ("concat", "concat(X, …)"), ("concat_ws", "concat_ws(SEP, X, …)"), ("format", "format(FORMAT, …)"), ("glob", "glob(X, Y)"),
        ("hex", "hex(X)"), ("ifnull", "ifnull(X, Y)"), ("iif", "iif(X, Y, Z)"), ("instr", "instr(X, Y)"),
        ("last_insert_rowid", "last_insert_rowid()"), ("length", "length(X)"), ("like", "like(X, Y)"), ("likelihood", "likelihood(X, Y)"),
        ("likely", "likely(X)"), ("lower", "lower(X)"), ("ltrim", "ltrim(X, Y)"), ("max", "max(X, …)"), ("min", "min(X, …)"),
        ("nullif", "nullif(X, Y)"), ("octet_length", "octet_length(X)"), ("printf", "printf(FORMAT, …)"), ("quote", "quote(X)"),
        ("random", "random()"), ("randomblob", "randomblob(N)"), ("replace", "replace(X, Y, Z)"), ("round", "round(X, Y)"),
        ("rtrim", "rtrim(X, Y)"), ("sign", "sign(X)"), ("sqlite_source_id", "sqlite_source_id()"), ("sqlite_version", "sqlite_version()"),
        ("substr", "substr(X, Y, Z)"), ("substring", "substring(X, Y, Z)"), ("total_changes", "total_changes()"), ("trim", "trim(X, Y)"),
        ("typeof", "typeof(X)"), ("unhex", "unhex(X)"), ("unicode", "unicode(X)"), ("unlikely", "unlikely(X)"), ("upper", "upper(X)"),
        ("zeroblob", "zeroblob(N)"),
        // aggregate
        ("avg", "avg(X)"), ("count", "count(X)"), ("group_concat", "group_concat(X, SEP)"), ("string_agg", "string_agg(X, SEP)"),
        ("sum", "sum(X)"), ("total", "total(X)"),
        // window
        ("row_number", "row_number()"), ("rank", "rank()"), ("dense_rank", "dense_rank()"), ("percent_rank", "percent_rank()"),
        ("cume_dist", "cume_dist()"), ("ntile", "ntile(N)"), ("lag", "lag(expr, offset, default)"), ("lead", "lead(expr, offset, default)"),
        ("first_value", "first_value(expr)"), ("last_value", "last_value(expr)"), ("nth_value", "nth_value(expr, N)"),
        // date and time
        ("date", "date(time, modifier, …)"), ("time", "time(time, modifier, …)"), ("datetime", "datetime(time, modifier, …)"),
        ("julianday", "julianday(time, modifier, …)"), ("unixepoch", "unixepoch(time, modifier, …)"),
        ("strftime", "strftime(format, time, modifier, …)"), ("timediff", "timediff(time1, time2)"),
        // math
        ("acos", "acos(X)"), ("asin", "asin(X)"), ("atan", "atan(X)"), ("atan2", "atan2(Y, X)"), ("ceil", "ceil(X)"),
        ("ceiling", "ceiling(X)"), ("cos", "cos(X)"), ("degrees", "degrees(X)"), ("exp", "exp(X)"), ("floor", "floor(X)"),
        ("ln", "ln(X)"), ("log", "log(B, X)"), ("log10", "log10(X)"), ("log2", "log2(X)"), ("mod", "mod(X, Y)"), ("pi", "pi()"),
        ("pow", "pow(X, Y)"), ("power", "power(X, Y)"), ("radians", "radians(X)"), ("sin", "sin(X)"), ("sqrt", "sqrt(X)"),
        ("tan", "tan(X)"), ("trunc", "trunc(X)"),
        // JSON
        ("json", "json(json)"), ("jsonb", "jsonb(json)"), ("json_array", "json_array(value, …)"), ("jsonb_array", "jsonb_array(value, …)"),
        ("json_array_length", "json_array_length(json, path)"), ("json_error_position", "json_error_position(json)"),
        ("json_extract", "json_extract(json, path, …)"), ("jsonb_extract", "jsonb_extract(json, path, …)"),
        ("json_insert", "json_insert(json, path, value, …)"), ("jsonb_insert", "jsonb_insert(json, path, value, …)"),
        ("json_object", "json_object(label, value, …)"), ("jsonb_object", "jsonb_object(label, value, …)"),
        ("json_patch", "json_patch(json1, json2)"), ("jsonb_patch", "jsonb_patch(json1, json2)"), ("json_pretty", "json_pretty(json)"),
        ("json_quote", "json_quote(value)"), ("json_remove", "json_remove(json, path, …)"), ("jsonb_remove", "jsonb_remove(json, path, …)"),
        ("json_replace", "json_replace(json, path, value, …)"), ("jsonb_replace", "jsonb_replace(json, path, value, …)"),
        ("json_set", "json_set(json, path, value, …)"), ("jsonb_set", "jsonb_set(json, path, value, …)"),
        ("json_type", "json_type(json, path)"), ("json_valid", "json_valid(json, flags)"),
        ("json_group_array", "json_group_array(value)"), ("jsonb_group_array", "jsonb_group_array(value)"),
        ("json_group_object", "json_group_object(label, value)"), ("jsonb_group_object", "jsonb_group_object(label, value)"),
        ("json_each", "json_each(json, path)"), ("json_tree", "json_tree(json, path)"),
    ];
}
