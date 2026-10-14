using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    private const string SystemSchemas = "'pg_catalog','information_schema'";

    public Task<PgSchemaTree> ListSchemaAsync(string secret, string? database, CancellationToken ct = default) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            string db;
            await using (var q = await QueryAsync(conn, tx, "SELECT current_database()", ct))
            {
                await q.Reader.ReadAsync(ct);
                db = q.Reader.GetString(0);
            }

            var names = new List<string>();
            await using (var q = await QueryAsync(conn, tx,
                $"SELECT nspname FROM pg_namespace WHERE nspname NOT IN ({SystemSchemas}) AND nspname NOT LIKE 'pg\\_%' ORDER BY nspname", ct))
            {
                while (await q.Reader.ReadAsync(ct))
                {
                    names.Add(q.Reader.GetString(0));
                }
            }

            var tables = new Dictionary<string, List<PgObjectNode>>();
            var views = new Dictionary<string, List<PgObjectNode>>();
            var matviews = new Dictionary<string, List<PgObjectNode>>();
            var functions = new Dictionary<string, List<PgObjectNode>>();

            await using (var q = await QueryAsync(conn, tx, $"""
                SELECT c.oid, n.nspname, c.relname, c.relkind, CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END
                  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE c.relkind IN ('r','v','m','p','f') AND n.nspname NOT IN ({SystemSchemas}) AND n.nspname NOT LIKE 'pg\_%'
                   AND NOT c.relispartition
                   AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
                 ORDER BY n.nspname, c.relname
                """, ct))
            {
                var r = q.Reader;
                while (await r.ReadAsync(ct))
                {
                    var schema = r.GetString(1);
                    var kind = KindOf(r.GetChar(3));
                    var node = new PgObjectNode(r.GetFieldValue<uint>(0), schema, r.GetString(2), kind,
                        r.IsDBNull(4) ? null : r.GetInt64(4));
                    var bucket = kind switch
                    {
                        PgObjectKind.View => views,
                        PgObjectKind.MaterializedView => matviews,
                        _ => tables,
                    };
                    Add(bucket, schema, node);
                }
            }

            await using (var q = await QueryAsync(conn, tx, $"""
                SELECT p.oid, n.nspname, p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname NOT IN ({SystemSchemas}) AND n.nspname NOT LIKE 'pg\_%' AND p.prokind IN ('f','p')
                   AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
                 ORDER BY 2, 3
                """, ct))
            {
                var r = q.Reader;
                while (await r.ReadAsync(ct))
                {
                    var schema = r.GetString(1);
                    Add(functions, schema, new PgObjectNode(r.GetFieldValue<uint>(0), schema, r.GetString(2), PgObjectKind.Function, null));
                }
            }

            var schemas = names.Select(n => new PgSchemaNode(n, Get(tables, n), Get(views, n), Get(matviews, n), Get(functions, n))).ToList();
            return new PgSchemaTree(db, schemas);
        }, ct);

    public Task<PgObjectDetail?> GetObjectDetailAsync(string secret, string? database, uint oid, CancellationToken ct = default) =>
        ReadAsync(secret, database, (conn, tx) => ReadDetailAsync(conn, tx, oid, ct), ct);

    public Task<string?> GetDdlAsync(string secret, string? database, uint oid, CancellationToken ct = default) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            var detail = await ReadDetailAsync(conn, tx, oid, ct);
            if (detail is null)
            {
                return null;
            }

            var qualified = PgIdent.Qualified(detail.Schema, detail.Name);
            switch (detail.Kind)
            {
                case PgObjectKind.Function:
                    return await ScalarTextAsync(conn, tx, "SELECT pg_get_functiondef($1)", oid, ct);
                case PgObjectKind.View or PgObjectKind.MaterializedView:
                    var def = await ScalarTextAsync(conn, tx, "SELECT pg_get_viewdef($1, true)", oid, ct);
                    var keyword = detail.Kind == PgObjectKind.View ? "VIEW" : "MATERIALIZED VIEW";
                    return $"CREATE {keyword} {qualified} AS\n{def}";
                default:
                    return TableDdl(detail, qualified);
            }
        }, ct);

    private static string TableDdl(PgObjectDetail d, string qualified)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(qualified).Append(" (\n");
        for (var i = 0; i < d.Columns.Count; i++)
        {
            var c = d.Columns[i];
            sb.Append("    ").Append(PgIdent.Quote(c.Name)).Append(' ').Append(c.Type);
            if (c.IsGeneratedStored && c.Default is not null)
            {
                sb.Append(" GENERATED ALWAYS AS (").Append(c.Default).Append(") STORED");
            }
            else if (c.Identity is 'a' or 'd')
            {
                sb.Append(c.Identity == 'a' ? " GENERATED ALWAYS AS IDENTITY" : " GENERATED BY DEFAULT AS IDENTITY");
            }
            else if (c.Default is not null)
            {
                sb.Append(" DEFAULT ").Append(c.Default);
            }

            if (!c.Nullable)
            {
                sb.Append(" NOT NULL");
            }

            sb.Append(i < d.Columns.Count - 1 ? ",\n" : "\n");
        }

        sb.Append(");\n");
        foreach (var con in d.Constraints)
        {
            sb.Append("ALTER TABLE ").Append(qualified).Append(" ADD CONSTRAINT ").Append(PgIdent.Quote(con.Name))
                .Append(' ').Append(con.Definition).Append(";\n");
        }

        foreach (var index in d.Indexes.Where(i => !i.BacksConstraint))
        {
            sb.Append(index.Definition).Append(";\n");
        }

        foreach (var c in d.Columns.Where(c => c.Comment is not null))
        {
            sb.Append("COMMENT ON COLUMN ").Append(qualified).Append('.').Append(PgIdent.Quote(c.Name))
                .Append(" IS '").Append(c.Comment!.Replace("'", "''", StringComparison.Ordinal)).Append("';\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static async Task<PgObjectDetail?> ReadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, uint oid, CancellationToken ct)
    {
        string schema, name, owner;
        PgObjectKind kind;
        long? estimate;
        long heap, indexBytes;
        await using (var cmd = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, c.relkind, CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END,
                   CASE WHEN c.relkind IN ('r','m','p') THEN pg_table_size(c.oid) ELSE 0 END,
                   CASE WHEN c.relkind IN ('r','m','p') THEN pg_indexes_size(c.oid) ELSE 0 END,
                   pg_get_userbyid(c.relowner)
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.oid = $1 AND c.relkind IN ('r','v','m','p','f')
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                await r.DisposeAsync();
                return await ReadFunctionDetailAsync(conn, tx, oid, ct);
            }

            schema = r.GetString(0);
            name = r.GetString(1);
            kind = KindOf(r.GetChar(2));
            estimate = r.IsDBNull(3) ? null : r.GetInt64(3);
            heap = r.GetInt64(4);
            indexBytes = r.GetInt64(5);
            owner = r.GetString(6);
        }

        var columns = new List<PgColumn>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT a.attnum, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(d.adbin, d.adrelid),
                   EXISTS (SELECT 1 FROM pg_constraint pk WHERE pk.conrelid = a.attrelid AND pk.contype = 'p' AND a.attnum = ANY (pk.conkey)),
                   (SELECT fk.confrelid::regclass::text || '(' ||
                           (SELECT fa.attname FROM pg_attribute fa
                             WHERE fa.attrelid = fk.confrelid AND fa.attnum = fk.confkey[array_position(fk.conkey, a.attnum)]) || ')'
                      FROM pg_constraint fk
                     WHERE fk.conrelid = a.attrelid AND fk.contype = 'f' AND a.attnum = ANY (fk.conkey)
                     ORDER BY fk.oid LIMIT 1),
                   col_description(a.attrelid, a.attnum), format_type(a.atttypid, NULL),
                   a.attgenerated = 's', a.attidentity
              FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
             WHERE a.attrelid = $1 AND a.attnum > 0 AND NOT a.attisdropped
             ORDER BY a.attnum
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var position = 0;
            while (await r.ReadAsync(ct))
            {
                position++;
                columns.Add(new PgColumn(position, r.GetString(1), r.GetString(2), r.GetBoolean(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.GetString(8), r.GetBoolean(9), r.GetChar(10)));
            }
        }

        var indexes = new List<PgIndex>();
        var constraints = new List<PgConstraint>();
        if (kind is PgObjectKind.Table or PgObjectKind.PartitionedTable or PgObjectKind.MaterializedView or PgObjectKind.ForeignTable)
        {
            await using (var cmd = new NpgsqlCommand("""
                SELECT i.indexrelid, ic.relname, pg_get_indexdef(i.indexrelid), am.amname, pg_relation_size(i.indexrelid),
                       coalesce(s.idx_scan, 0), coalesce(s.idx_tup_read, 0), i.indisprimary, i.indisunique,
                       i.indpred IS NOT NULL, i.indisvalid,
                       EXISTS (SELECT 1 FROM pg_constraint WHERE conindid = i.indexrelid)
                  FROM pg_index i
                  JOIN pg_class ic ON ic.oid = i.indexrelid
                  JOIN pg_am am ON am.oid = ic.relam
                  LEFT JOIN pg_stat_user_indexes s ON s.indexrelid = i.indexrelid
                 WHERE i.indrelid = $1
                 ORDER BY i.indisprimary DESC, ic.relname
                """, conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var def = r.GetString(2);
                    indexes.Add(new PgIndex(r.GetFieldValue<uint>(0), r.GetString(1), def, ColumnsText(def, r.GetString(3)), r.GetString(3),
                        r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetBoolean(7), r.GetBoolean(8),
                        r.GetBoolean(9), r.GetBoolean(10), r.GetBoolean(11)));
                }
            }

            await using (var cmd = new NpgsqlCommand("""
                SELECT conname, contype, pg_get_constraintdef(oid, true) FROM pg_constraint
                 WHERE conrelid = $1 AND contype IN ('p','f','u','c','x')
                 ORDER BY CASE contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 WHEN 'f' THEN 2 WHEN 'c' THEN 3 ELSE 4 END, conname
                """, conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var type = r.GetChar(1) switch
                    {
                        'p' => "primary key",
                        'f' => "foreign key",
                        'u' => "unique",
                        'c' => "check",
                        _ => "exclusion",
                    };
                    constraints.Add(new PgConstraint(r.GetString(0), type, r.GetString(2)));
                }
            }
        }

        return new PgObjectDetail(oid, schema, name, kind, estimate, heap, indexBytes, owner, columns, indexes,
            constraints, await StatsResetAsync(conn, tx, ct));
    }

    private static async Task<PgObjectDetail?> ReadFunctionDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, uint oid, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT n.nspname, p.proname, pg_get_userbyid(p.proowner)
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE p.oid = $1 AND p.prokind IN ('f','p')
            """, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new PgObjectDetail(oid, r.GetString(0), r.GetString(1), PgObjectKind.Function, null, 0, 0, r.GetString(2),
                [], [], [], null)
            : null;
    }

    private static async Task<DateTimeOffset?> StatsResetAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT stats_reset FROM pg_stat_database WHERE datname = current_database()", conn, tx);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : new DateTimeOffset((DateTime)value, TimeSpan.Zero);
    }

    private static async Task<string?> ScalarTextAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, uint oid, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Oid, Value = oid });
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>"(status)" / "(a, b) WHERE (c > 0)": everything from the column list on, skipping the index name.</summary>
    private static string ColumnsText(string definition, string method)
    {
        var marker = " USING " + method + " (";
        var using_ = definition.LastIndexOf(marker, StringComparison.Ordinal);
        var open = using_ < 0 ? definition.IndexOf('(') : using_ + marker.Length - 1;
        return open < 0 ? "" : definition[open..];
    }

    private static PgObjectKind KindOf(char relkind) => relkind switch
    {
        'v' => PgObjectKind.View,
        'm' => PgObjectKind.MaterializedView,
        'p' => PgObjectKind.PartitionedTable,
        'f' => PgObjectKind.ForeignTable,
        _ => PgObjectKind.Table,
    };

    private static void Add(Dictionary<string, List<PgObjectNode>> map, string schema, PgObjectNode node)
    {
        if (!map.TryGetValue(schema, out var list))
        {
            map[schema] = list = [];
        }

        list.Add(node);
    }

    private static IReadOnlyList<PgObjectNode> Get(Dictionary<string, List<PgObjectNode>> map, string schema) =>
        map.TryGetValue(schema, out var list) ? list : [];
}
