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

    // PostgreSQL permits these inside a READ ONLY transaction, yet they take heavy locks or load code.
    private static readonly HashSet<string> ReadMaintenance = new(StringComparer.Ordinal)
    {
        "CLUSTER", "REINDEX", "ANALYZE", "LOAD",
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

            if (!write && ReadMaintenance.Contains(statement.FirstKeyword))
            {
                throw new PgSqlRefusedException($"`{statement.FirstKeyword}` can't run here — use Maintenance or Browse.");
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

    /// <summary>
    /// Read-mode check for the EXPLAIN tab, which is not audited: EXPLAIN ANALYZE executes the statement, so it is refused
    /// here and left to Run (audited).
    /// </summary>
    public static IReadOnlyList<PgStatement> CheckExplain(string sql)
    {
        var statements = Check(sql, write: false);
        if (statements[0].FirstKeyword == "EXPLAIN" && ExplainAnalyze().IsMatch(statements[0].Body))
        {
            throw new PgSqlRefusedException("EXPLAIN ANALYZE runs the statement — use Run, which is audited.");
        }

        return statements;
    }

    [GeneratedRegex(@"^EXPLAIN\s*(ANALY[ZS]E\b|\([^)]*\bANALY[ZS]E\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ExplainAnalyze();

    [GeneratedRegex(@"^\s*CREATE\s+(UNIQUE\s+)?INDEX\s+CONCURRENTLY\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreateIndexConcurrently();

    [GeneratedRegex(@"^\s*REINDEX\b[^;]*\bCONCURRENTLY\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReindexConcurrently();

    [GeneratedRegex(@"^\s*(CREATE|DROP)\s+DATABASE\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreateOrDropDatabase();

    [GeneratedRegex(@"^\s*ALTER\s+SYSTEM\b", RegexOptions.IgnoreCase)]
    private static partial Regex AlterSystem();
}
