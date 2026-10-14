using System.Globalization;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    // Partitions are left out as in ListSchemaAsync; the parent covers them.
    private static readonly string CompletionObjectsSql = $"""
        WITH objs AS (
            SELECT c.oid, n.nspname, c.relname, c.relkind
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind IN ('r','v','m','p','f') AND n.nspname NOT IN ({SystemSchemas}) AND n.nspname NOT LIKE 'pg\_%'
               AND NOT c.relispartition
             ORDER BY n.nspname, c.relname
             LIMIT {PgCompletionSchema.MaxObjects.ToString(CultureInfo.InvariantCulture)})
        SELECT o.nspname, o.relname, o.relkind, a.attname, format_type(a.atttypid, a.atttypmod),
               COALESCE(a.attnum = ANY (pk.indkey), false)
          FROM objs o
          LEFT JOIN pg_attribute a ON a.attrelid = o.oid AND a.attnum > 0 AND NOT a.attisdropped
          LEFT JOIN pg_index pk ON pk.indrelid = o.oid AND pk.indisprimary
         ORDER BY o.nspname, o.relname, a.attnum
        """;

    public Task<PgCompletionSchema> GetCompletionSchemaAsync(string secret, string? database, CancellationToken ct = default) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            string[] searchPath;
            await using (var q = await QueryAsync(conn, tx, "SELECT current_schemas(false)", ct))
            {
                await q.Reader.ReadAsync(ct);
                searchPath = q.Reader.GetFieldValue<string[]>(0);
            }

            var schemas = new List<string>();
            await using (var q = await QueryAsync(conn, tx,
                $"SELECT nspname FROM pg_namespace WHERE nspname NOT IN ({SystemSchemas}) AND nspname NOT LIKE 'pg\\_%' ORDER BY nspname", ct))
            {
                while (await q.Reader.ReadAsync(ct))
                {
                    schemas.Add(q.Reader.GetString(0));
                }
            }

            var objects = new List<PgCompletionObject>();
            await using (var q = await QueryAsync(conn, tx, CompletionObjectsSql, ct))
            {
                var r = q.Reader;
                List<PgCompletionColumn>? columns = null;
                string? lastSchema = null;
                string? lastName = null;
                while (await r.ReadAsync(ct))
                {
                    var schema = r.GetString(0);
                    var name = r.GetString(1);
                    if (columns is null || schema != lastSchema || name != lastName)
                    {
                        columns = [];
                        objects.Add(new PgCompletionObject(schema, name, KindOf(r.GetChar(2)), columns));
                        lastSchema = schema;
                        lastName = name;
                    }

                    if (!r.IsDBNull(3) && columns.Count < PgCompletionSchema.MaxColumns)
                    {
                        columns.Add(new PgCompletionColumn(r.GetString(3), r.GetString(4), r.GetBoolean(5)));
                    }
                }
            }

            return new PgCompletionSchema(searchPath, schemas, objects);
        }, ct);
}
