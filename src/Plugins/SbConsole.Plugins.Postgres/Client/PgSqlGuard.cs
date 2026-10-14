using System.Text.RegularExpressions;

namespace SbConsole.Plugins.Postgres.Client;

/// <summary>Rejects SQL the console can't run safely inside the transaction it manages.</summary>
public static partial class PgSqlGuard
{
    private static readonly HashSet<string> ReadRefused = new(StringComparer.Ordinal)
    {
        "BEGIN", "START", "COMMIT", "END", "ROLLBACK", "ABORT", "SAVEPOINT", "RELEASE", "PREPARE", "SET", "RESET",
        "DISCARD", "LISTEN", "UNLISTEN", "NOTIFY", "COPY", "VACUUM", "CHECKPOINT", "DO", "CALL", "LOCK",
    };

    private static readonly HashSet<string> WriteRefused = new(StringComparer.Ordinal)
    {
        "BEGIN", "START", "COMMIT", "END", "ROLLBACK", "ABORT", "SAVEPOINT", "RELEASE", "PREPARE", "VACUUM", "COPY",
        "CHECKPOINT",
    };

    private static readonly (Regex Pattern, string Label)[] NoTransaction =
    [
        (CreateIndexConcurrently(), "CREATE INDEX CONCURRENTLY"),
        (ReindexConcurrently(), "REINDEX … CONCURRENTLY"),
        (CreateOrDropDatabase(), "CREATE/DROP DATABASE"),
        (AlterSystem(), "ALTER SYSTEM"),
    ];

    public static IReadOnlyList<PgStatement> Check(string sql, bool write)
    {
        var statements = PgSqlLexer.Split(sql);
        if (statements.Count == 0)
        {
            throw new PgSqlRefusedException("Nothing to run.");
        }

        if (!write && statements.Count > 1)
        {
            throw new PgSqlRefusedException(
                $"Read-only mode runs one statement at a time — this has {statements.Count}. Switch to Write to run a script.");
        }

        var refused = write ? WriteRefused : ReadRefused;
        foreach (var statement in statements)
        {
            if (statement.FirstKeyword == "VACUUM")
            {
                throw new PgSqlRefusedException("VACUUM can't run in a transaction — use Maintenance.");
            }

            if (refused.Contains(statement.FirstKeyword))
            {
                throw new PgSqlRefusedException(
                    $"`{statement.FirstKeyword}` can't run here — the console manages the transaction.");
            }

            if (write)
            {
                foreach (var (pattern, label) in NoTransaction)
                {
                    if (pattern.IsMatch(statement.Body))
                    {
                        throw new PgSqlRefusedException(
                            $"`{label}` can't run in a transaction — run it from a SQL client.");
                    }
                }
            }
        }

        return statements;
    }

    [GeneratedRegex(@"^\s*CREATE\s+(UNIQUE\s+)?INDEX\s+CONCURRENTLY\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreateIndexConcurrently();

    [GeneratedRegex(@"^\s*REINDEX\b[^;]*\bCONCURRENTLY\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReindexConcurrently();

    [GeneratedRegex(@"^\s*(CREATE|DROP)\s+DATABASE\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreateOrDropDatabase();

    [GeneratedRegex(@"^\s*ALTER\s+SYSTEM\b", RegexOptions.IgnoreCase)]
    private static partial Regex AlterSystem();
}
