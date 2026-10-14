using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    public Task<string> ReindexIndexAsync(string secret, string? database, uint indexOid, CancellationToken ct = default) =>
        RunIndexDdlAsync(secret, database, indexOid, "REINDEX INDEX CONCURRENTLY", refuseConstraintBacked: false, ct);

    public Task<string> DropIndexAsync(string secret, string? database, uint indexOid, CancellationToken ct = default) =>
        RunIndexDdlAsync(secret, database, indexOid, "DROP INDEX CONCURRENTLY", refuseConstraintBacked: true, ct);

    // CONCURRENTLY cannot run inside a transaction block, so this uses a plain autocommit connection (not ReadAsync).
    // verb is a literal from the callers above; the name is resolved from the catalog by oid and quoted.
    private static async Task<string> RunIndexDdlAsync(
        string secret, string? database, uint indexOid, string verb, bool refuseConstraintBacked, CancellationToken ct)
    {
        var settings = PostgresConnectionSettings.Parse(secret);
        if (settings.Mode != PgAccessMode.ReadWrite)
        {
            throw new PgSqlRefusedException("This connection is read-only — set Mode to Read-write in Connections to change indexes.");
        }

        await using var conn = Open(settings, database);
        await conn.OpenAsync(ct);

        string qualified;
        bool backsConstraint;
        await using (var find = new NpgsqlCommand(
            "SELECT n.nspname, c.relname, EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = c.oid) " +
            "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = @oid AND c.relkind = 'i'", conn))
        {
            find.Parameters.Add(new NpgsqlParameter("oid", NpgsqlTypes.NpgsqlDbType.Oid) { Value = indexOid });
            await using var r = await find.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                throw new PgSqlRefusedException("That index no longer exists.");
            }

            qualified = PgIdent.Qualified(r.GetString(0), r.GetString(1));
            backsConstraint = r.GetBoolean(2);
        }

        if (refuseConstraintBacked && backsConstraint)
        {
            throw new PgSqlRefusedException("This index backs a constraint — drop the constraint instead.");
        }

        await using (var set = new NpgsqlCommand("SET statement_timeout = '1h'", conn))
        {
            await set.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand($"{verb} {qualified}", conn) { CommandTimeout = 3600 + 5 };
        await cmd.ExecuteNonQueryAsync(ct);
        return qualified;
    }
}
