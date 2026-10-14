namespace SbConsole.Plugins.Postgres.Query;

/// <summary>
/// PostgreSQL 16 keywords worth suggesting (reserved plus common non-reserved), the reserved words an
/// identifier must be quoted to use, and the built-in functions offered (design spec §16). Operators
/// (->, ->>, @>) are not functions and are not listed; types are out of scope.
/// </summary>
internal static class PgCompletionVocabulary
{
    public static readonly string[] Keywords =
    [
        "ADD", "ALL", "ALTER", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "AUTHORIZATION", "BEGIN", "BETWEEN",
        "BOTH", "BY", "CASCADE", "CASE", "CAST", "CHECK", "CLUSTER", "COLLATE", "COLUMN", "COMMENT", "COMMIT", "CONCURRENTLY",
        "CONFLICT", "CONSTRAINT", "COPY", "CREATE", "CROSS", "CURRENT_DATE", "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_TIME",
        "CURRENT_TIMESTAMP", "CURRENT_USER", "DATABASE", "DEFAULT", "DEFERRABLE", "DELETE", "DESC", "DISTINCT", "DO", "DROP",
        "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE", "EXISTS", "EXPLAIN", "EXTENSION", "FALSE", "FETCH", "FILTER", "FIRST",
        "FOLLOWING", "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION", "GRANT", "GROUP", "GROUPS", "HAVING", "IF", "ILIKE", "IN",
        "INDEX", "INNER", "INSERT", "INTERSECT", "INTERVAL", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LAST", "LATERAL", "LEADING",
        "LEFT", "LIKE", "LIMIT", "LOCAL", "LOCALTIME", "LOCALTIMESTAMP", "LOCK", "MATCHED", "MATERIALIZED", "MERGE", "NATURAL", "NEXT", "NO", "NOT",
        "NOTHING", "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OTHERS", "OUTER", "OVER", "OVERLAPS", "OVERRIDING",
        "OWNER", "PARTITION", "PRECEDING", "PRIMARY", "PROCEDURE", "RANGE", "RECURSIVE", "REFERENCES", "REFRESH", "REINDEX",
        "RENAME", "REPLACE", "RESTRICT", "RETURNING", "RETURNS", "REVOKE", "RIGHT", "ROLLBACK", "ROW", "ROWS", "SAVEPOINT",
        "SCHEMA", "SELECT", "SEQUENCE", "SESSION_USER", "SET", "SHOW", "SIMILAR", "SOME", "SYMMETRIC", "TABLE", "TABLESAMPLE",
        "TEMP", "TEMPORARY", "THEN", "TIES", "TO", "TRAILING", "TRANSACTION", "TRIGGER", "TRUE", "TRUNCATE", "TYPE", "UNBOUNDED",
        "UNION", "UNIQUE", "UPDATE", "USER", "USING", "VACUUM", "VALUES", "VARIADIC", "VERBOSE", "VIEW", "WHEN", "WHERE",
        "WINDOW", "WITH", "WITHIN", "WITHOUT",
    ];

    public static readonly HashSet<string> KeywordSet = new(Keywords, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reserved (and reserved-can-be-function-or-type) words: an identifier spelled like one must be quoted.</summary>
    public static readonly HashSet<string> ReservedSet = new(
    [
        "ALL", "ANALYSE", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "AUTHORIZATION", "BINARY", "BOTH", "CASE",
        "CAST", "CHECK", "COLLATE", "COLLATION", "COLUMN", "CONCURRENTLY", "CONSTRAINT", "CREATE", "CROSS", "CURRENT_CATALOG",
        "CURRENT_DATE", "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DEFAULT",
        "DEFERRABLE", "DESC", "DISTINCT", "DO", "ELSE", "END", "EXCEPT", "FALSE", "FETCH", "FOR", "FOREIGN", "FREEZE", "FROM",
        "FULL", "GRANT", "GROUP", "HAVING", "ILIKE", "IN", "INITIALLY", "INNER", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN",
        "LATERAL", "LEADING", "LEFT", "LIKE", "LIMIT", "LOCALTIME", "LOCALTIMESTAMP", "NATURAL", "NOT", "NOTNULL", "NULL",
        "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER", "OVERLAPS", "PLACING", "PRIMARY", "REFERENCES", "RETURNING", "RIGHT",
        "SELECT", "SESSION_USER", "SIMILAR", "SOME", "SYMMETRIC", "SYSTEM_USER", "TABLE", "TABLESAMPLE", "THEN", "TO",
        "TRAILING", "TRUE", "UNION", "UNIQUE", "USER", "USING", "VARIADIC", "VERBOSE", "WHEN", "WHERE", "WINDOW", "WITH",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Name and signature; a signature ending in "()" takes no arguments.</summary>
    public static readonly (string Name, string Signature)[] Functions =
    [
        // aggregate
        ("avg", "avg(expr)"), ("count", "count(expr)"), ("sum", "sum(expr)"), ("min", "min(expr)"), ("max", "max(expr)"),
        ("array_agg", "array_agg(expr)"), ("string_agg", "string_agg(expr, delimiter)"), ("bool_and", "bool_and(expr)"),
        ("bool_or", "bool_or(expr)"), ("every", "every(expr)"), ("json_agg", "json_agg(expr)"), ("jsonb_agg", "jsonb_agg(expr)"),
        ("json_object_agg", "json_object_agg(key, value)"), ("jsonb_object_agg", "jsonb_object_agg(key, value)"),
        ("percentile_cont", "percentile_cont(fraction)"), ("percentile_disc", "percentile_disc(fraction)"), ("mode", "mode()"),
        ("stddev", "stddev(expr)"), ("variance", "variance(expr)"),
        // window
        ("row_number", "row_number()"), ("rank", "rank()"), ("dense_rank", "dense_rank()"), ("percent_rank", "percent_rank()"),
        ("cume_dist", "cume_dist()"), ("ntile", "ntile(n)"), ("lag", "lag(value, offset, default)"),
        ("lead", "lead(value, offset, default)"), ("first_value", "first_value(value)"), ("last_value", "last_value(value)"),
        ("nth_value", "nth_value(value, n)"),
        // conditional
        ("coalesce", "coalesce(value, …)"), ("nullif", "nullif(value1, value2)"), ("greatest", "greatest(value, …)"),
        ("least", "least(value, …)"),
        // string
        ("length", "length(text)"), ("char_length", "char_length(text)"), ("lower", "lower(text)"), ("upper", "upper(text)"),
        ("initcap", "initcap(text)"), ("trim", "trim(text)"), ("btrim", "btrim(text, characters)"),
        ("ltrim", "ltrim(text, characters)"), ("rtrim", "rtrim(text, characters)"), ("lpad", "lpad(text, length, fill)"),
        ("rpad", "rpad(text, length, fill)"), ("concat", "concat(value, …)"), ("concat_ws", "concat_ws(sep, value, …)"),
        ("format", "format(formatstr, …)"), ("left", "left(text, n)"), ("right", "right(text, n)"),
        ("substring", "substring(text, start, count)"), ("substr", "substr(text, start, count)"),
        ("position", "position(substring in text)"), ("strpos", "strpos(text, substring)"), ("replace", "replace(text, from, to)"),
        ("split_part", "split_part(text, delimiter, n)"), ("string_to_array", "string_to_array(text, delimiter)"),
        ("regexp_replace", "regexp_replace(text, pattern, replacement, flags)"), ("regexp_match", "regexp_match(text, pattern)"),
        ("regexp_matches", "regexp_matches(text, pattern, flags)"), ("regexp_split_to_table", "regexp_split_to_table(text, pattern)"),
        ("repeat", "repeat(text, n)"), ("reverse", "reverse(text)"), ("md5", "md5(text)"), ("encode", "encode(bytes, format)"),
        ("decode", "decode(text, format)"), ("to_hex", "to_hex(n)"), ("quote_ident", "quote_ident(text)"),
        ("quote_literal", "quote_literal(text)"), ("starts_with", "starts_with(text, prefix)"),
        // math
        ("abs", "abs(x)"), ("ceil", "ceil(x)"), ("floor", "floor(x)"), ("round", "round(x, s)"), ("trunc", "trunc(x, s)"),
        ("mod", "mod(y, x)"), ("div", "div(y, x)"), ("power", "power(a, b)"), ("sqrt", "sqrt(x)"), ("exp", "exp(x)"),
        ("ln", "ln(x)"), ("log", "log(x)"), ("sign", "sign(x)"), ("random", "random()"),
        // date and time
        ("now", "now()"), ("clock_timestamp", "clock_timestamp()"), ("statement_timestamp", "statement_timestamp()"),
        ("transaction_timestamp", "transaction_timestamp()"), ("date_trunc", "date_trunc(field, source)"),
        ("date_part", "date_part(field, source)"), ("extract", "extract(field from source)"), ("age", "age(timestamp, timestamp)"),
        ("to_char", "to_char(value, format)"), ("to_date", "to_date(text, format)"), ("to_timestamp", "to_timestamp(text, format)"),
        ("make_date", "make_date(year, month, day)"), ("make_interval", "make_interval(years, months, weeks, days, hours, mins, secs)"),
        ("date_bin", "date_bin(stride, source, origin)"), ("justify_interval", "justify_interval(interval)"),
        // json
        ("to_json", "to_json(value)"), ("to_jsonb", "to_jsonb(value)"), ("row_to_json", "row_to_json(record)"),
        ("json_build_object", "json_build_object(key, value, …)"), ("jsonb_build_object", "jsonb_build_object(key, value, …)"),
        ("json_build_array", "json_build_array(value, …)"), ("jsonb_build_array", "jsonb_build_array(value, …)"),
        ("jsonb_set", "jsonb_set(target, path, new_value, create_missing)"), ("jsonb_insert", "jsonb_insert(target, path, new_value)"),
        ("jsonb_strip_nulls", "jsonb_strip_nulls(jsonb)"), ("jsonb_pretty", "jsonb_pretty(jsonb)"), ("jsonb_typeof", "jsonb_typeof(jsonb)"),
        ("json_typeof", "json_typeof(json)"), ("jsonb_array_length", "jsonb_array_length(jsonb)"),
        ("json_array_length", "json_array_length(json)"), ("jsonb_array_elements", "jsonb_array_elements(jsonb)"),
        ("jsonb_array_elements_text", "jsonb_array_elements_text(jsonb)"), ("json_array_elements", "json_array_elements(json)"),
        ("jsonb_each", "jsonb_each(jsonb)"), ("jsonb_each_text", "jsonb_each_text(jsonb)"), ("jsonb_object_keys", "jsonb_object_keys(jsonb)"),
        ("jsonb_extract_path", "jsonb_extract_path(from_json, path, …)"),
        ("jsonb_extract_path_text", "jsonb_extract_path_text(from_json, path, …)"),
        ("jsonb_path_query", "jsonb_path_query(target, path)"), ("jsonb_path_exists", "jsonb_path_exists(target, path)"),
        // array
        ("array_length", "array_length(array, dim)"), ("array_append", "array_append(array, element)"),
        ("array_prepend", "array_prepend(element, array)"), ("array_cat", "array_cat(array1, array2)"),
        ("array_remove", "array_remove(array, element)"), ("array_position", "array_position(array, element)"),
        ("array_to_string", "array_to_string(array, delimiter)"), ("cardinality", "cardinality(array)"), ("unnest", "unnest(array)"),
        // set-returning and system
        ("generate_series", "generate_series(start, stop, step)"), ("gen_random_uuid", "gen_random_uuid()"),
        ("current_setting", "current_setting(name)"), ("pg_typeof", "pg_typeof(value)"), ("pg_size_pretty", "pg_size_pretty(bytes)"),
        ("pg_total_relation_size", "pg_total_relation_size(relation)"), ("pg_relation_size", "pg_relation_size(relation)"),
        ("version", "version()"), ("current_database", "current_database()"), ("pg_backend_pid", "pg_backend_pid()"),
        ("txid_current", "txid_current()"), ("pg_sleep", "pg_sleep(seconds)"),
    ];
}
