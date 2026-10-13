using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// Tables page DDL (mockups 1b / 1f, design spec §11.5). Create runs user SQL, so it is
/// attach-limited (UserWrite); Rename and Drop are plugin-owned SQL whose names are checked against
/// sqlite_schema and only ever quoted into the statement (PluginWrite). Each runs in its own
/// BEGIN IMMEDIATE … COMMIT and rolls back on any error.
/// </summary>
public sealed partial class SqliteOperations
{
    public const int MaxTableNameLength = 128;

    public Task<string> CreateTableAsync(string secret, string ddl, CancellationToken ct = default) =>
        RunScriptAsync(secret, ddl, SqliteOpenOptions.UserWrite, (c, script) =>
        {
            if (script.Statements is not [{ } statement] || statement.IsExplain || !CreateTablePrefix().IsMatch(statement.Text))
            {
                throw new SqliteDdlRefusedException("Create table takes exactly one CREATE TABLE statement — use Query for anything else.");
            }

            return InTransaction(c, () =>
            {
                var before = UserTables(c);
                SqliteSql.Execute(c, statement.Text);
                var added = UserTables(c).Where(t => !before.Contains(t)).ToList();
                return added is [{ } name]
                    ? name
                    : throw new SqliteDdlRefusedException(added.Count == 0
                        ? "The statement didn't create a table in this file — does it already exist?"
                        : "The statement created more than one table — use Query for that.");
            });
        }, ct);

    public Task RenameTableAsync(string secret, string oldName, string newName, CancellationToken ct = default) =>
        RunAsync(secret, SqliteOpenOptions.PluginWrite, c =>
        {
            var table = RequireUserTable(c, oldName, "renamed");
            var target = ValidateNewTableName(c, table, newName);
            return InTransaction(c, () =>
            {
                SqliteSql.Execute(c, $"ALTER TABLE {SqliteSql.QuoteIdentifier(table)} RENAME TO {SqliteSql.QuoteIdentifier(target)}");
                return true;
            });
        }, ct);

    public Task<DropImpact> GetDropImpactAsync(string secret, string name, CancellationToken ct = default) =>
        RunAsync(secret, c =>
        {
            var table = RequireUserTable(c, name, "dropped");
            var indexes = (int)SqliteSql.Long(c, "SELECT COUNT(*) FROM pragma_index_list($name)", ("$name", table));
            var references = new List<DropReference>();
            foreach (var other in UserTables(c).Where(t => !string.Equals(t, table, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var keys = SqliteSql.Rows(c,
                    "SELECT id, \"from\", on_delete FROM pragma_foreign_key_list($name) WHERE \"table\" = $target COLLATE NOCASE ORDER BY id, seq",
                    r => (Id: r.GetInt32(0), From: r.GetString(1), OnDelete: r.GetString(2)), ("$name", other), ("$target", table));
                references.AddRange(keys.GroupBy(k => k.Id)
                    .Select(g => new DropReference(other, g.Select(k => k.From).ToList(), g.First().OnDelete)));
            }

            return new DropImpact(table, Lookup(ReadStat1Rows(c), table), indexes, references);
        }, ct);

    public Task DropTableAsync(string secret, string name, CancellationToken ct = default) =>
        RunAsync(secret, SqliteOpenOptions.PluginWrite, c =>
        {
            var table = RequireUserTable(c, name, "dropped");
            return InTransaction(c, () =>
            {
                // foreign_keys is on: the implicit DELETE cascades, clears, or fails on a RESTRICT/NO ACTION reference.
                SqliteSql.Execute(c, $"DROP TABLE {SqliteSql.QuoteIdentifier(table)}");
                return true;
            });
        }, ct);

    /// <summary>The exact sqlite_schema name of a user table (not a view, not sqlite_*), or a friendly refusal.</summary>
    private static string RequireUserTable(SqliteConnection c, string name, string verb)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SqliteDdlRefusedException("Pick a table first.");
        }

        if (IsInternalName(name))
        {
            throw new SqliteDdlRefusedException($"'{name}' belongs to SQLite itself and can't be {verb} here.");
        }

        var type = SqliteSql.Scalar(c, "SELECT type FROM sqlite_schema WHERE name = $name AND type IN ('table', 'view')", ("$name", name)) as string;
        return type switch
        {
            "table" => name,
            "view" => throw new SqliteDdlRefusedException($"'{name}' is a view — only tables can be {verb} here."),
            _ => throw new SqliteObjectNotFoundException(name),
        };
    }

    /// <summary>Spec §11.5: non-empty, ≤ 128 characters, not sqlite_-prefixed, not taken (SQLite compares names case-insensitively).</summary>
    private static string ValidateNewTableName(SqliteConnection c, string oldName, string newName)
    {
        var target = newName?.Trim() ?? "";
        if (target.Length == 0)
        {
            throw new SqliteDdlRefusedException("Type a new name.");
        }

        if (target.Length > MaxTableNameLength)
        {
            throw new SqliteDdlRefusedException($"Table names are limited to {MaxTableNameLength} characters here.");
        }

        if (IsInternalName(target))
        {
            throw new SqliteDdlRefusedException("Names starting with sqlite_ are reserved for SQLite itself.");
        }

        if (string.Equals(target, oldName, StringComparison.OrdinalIgnoreCase))
        {
            // SQLite refuses a rename to a different case of the same name, too.
            throw new SqliteDdlRefusedException("That is already the table's name — SQLite compares names case-insensitively.");
        }

        var taken = SqliteSql.Long(c, "SELECT COUNT(*) FROM sqlite_schema WHERE name = $name COLLATE NOCASE", ("$name", target));
        return taken > 0 ? throw new SqliteDdlRefusedException($"'{target}' is already taken by another table, view, index or trigger.") : target;
    }

    private static bool IsInternalName(string name) => name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> UserTables(SqliteConnection c) =>
        SqliteSql.Rows(c, @"SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\'", r => r.GetString(0))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>BEGIN IMMEDIATE, <paramref name="work"/>, COMMIT; any error rolls back (plain SQL, as ExecuteWrite does).</summary>
    private T InTransaction<T>(SqliteConnection c, Func<T> work)
    {
        SqliteSql.Execute(c, "BEGIN IMMEDIATE");
        try
        {
            var result = work();
            SqliteSql.Execute(c, "COMMIT");
            return result;
        }
        catch
        {
            RollbackQuietly(c);
            throw;
        }
    }

    // CREATE [TEMP|TEMPORARY] TABLE after leading comments; TEMP never reaches the file, so the
    // sqlite_schema diff refuses it afterwards. CREATE VIRTUAL TABLE doesn't match.
    [GeneratedRegex(@"\A(?:\s+|--[^\n]*(?:\n|\z)|/\*.*?\*/)*CREATE\s+(?:TEMP\s+|TEMPORARY\s+)?TABLE\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CreateTablePrefix();
}
