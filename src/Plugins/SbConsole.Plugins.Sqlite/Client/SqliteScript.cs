using SQLitePCL;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// One statement of a user script. Text is trimmed, without its terminating ';'. Keyword is the
/// first word (upper case, after comments) -- for EXPLAIN [QUERY PLAN] statements the explained
/// statement's keyword, with IsExplain set and Body starting at that keyword. Empty when the
/// statement doesn't start with a word.
/// </summary>
public sealed record SqliteStatement(string Text, string Keyword, bool IsExplain, string Body);

/// <summary>
/// Splits user scripts into statements (design spec §11.2): a small tokenizer skips single, double,
/// backtick and bracket quotes and -- / /* */ comments, and a ';' outside them ends a statement
/// only when sqlite3_complete agrees the text so far is complete -- which keeps a
/// CREATE TRIGGER … BEGIN …; END body together. Parse also refuses, before anything runs, what the
/// console must own: ATTACH/DETACH (the path policy), VACUUM (Backups) and transaction control.
/// </summary>
public sealed record SqliteScript(IReadOnlyList<SqliteStatement> Statements)
{
    private static readonly HashSet<string> TransactionControl =
        new(["BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE"], StringComparer.Ordinal);

    static SqliteScript() => Batteries_V2.Init(); // sqlite3_complete may run before any connection opened

    /// <summary>Splits and validates; throws SqliteScriptRefusedException with the friendly reason.</summary>
    public static SqliteScript Parse(string sql)
    {
        var statements = Split(sql);
        if (statements.Count == 0)
        {
            throw new SqliteScriptRefusedException("The script is empty — type at least one statement.");
        }

        for (var i = 0; i < statements.Count; i++)
        {
            var keyword = statements[i].Keyword;
            var why = keyword switch
            {
                "ATTACH" or "DETACH" => "the console only opens the connection's own file.",
                "VACUUM" => "use Backups for a compacted copy.",
                _ when TransactionControl.Contains(keyword) => "the console runs every script in its own transaction.",
                _ => null,
            };
            if (why is not null)
            {
                throw new SqliteScriptRefusedException($"{keyword} is not allowed (statement {i + 1}) — {why}");
            }
        }

        return new SqliteScript(statements);
    }

    /// <summary>Splits without validating. Pieces that are only whitespace and comments are dropped.</summary>
    public static IReadOnlyList<SqliteStatement> Split(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var statements = new List<SqliteStatement>();
        var start = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var ch = sql[i];
            if (ch == ';')
            {
                if (raw.sqlite3_complete(sql[start..(i + 1)]) != 0)
                {
                    Add(statements, sql[start..i]);
                    start = i + 1;
                }

                i++;
            }
            else
            {
                i = SkipToken(sql, i);
            }
        }

        Add(statements, sql[start..]);
        return statements;
    }

    private static void Add(List<SqliteStatement> statements, string piece)
    {
        var text = piece.Trim();
        if (SkipTrivia(text, 0) == text.Length)
        {
            return; // nothing but whitespace and comments
        }

        var words = Words(text).Take(4).ToList();
        var skip = words is [{ Word: "EXPLAIN" }, ..]
            ? words is [_, { Word: "QUERY" }, { Word: "PLAN" }, ..] ? 3 : 1
            : 0;
        statements.Add(skip == 0
            ? new SqliteStatement(text, words.Count > 0 ? words[0].Word : "", false, text)
            : words.Count > skip
                ? new SqliteStatement(text, words[skip].Word, true, text[words[skip].Start..])
                : new SqliteStatement(text, "", true, text));
    }

    /// <summary>
    /// The leading run of bare words (from the first code character), each with its start offset,
    /// skipping comments between them. Stops at the first token that is not a bare word, so a
    /// quoted "attach" is never a keyword.
    /// </summary>
    private static IEnumerable<(string Word, int Start)> Words(string text)
    {
        var i = 0;
        while (true)
        {
            i = SkipTrivia(text, i);
            if (i >= text.Length || !IsWordStart(text[i]))
            {
                yield break;
            }

            var start = i;
            while (i < text.Length && IsWordPart(text[i]))
            {
                i++;
            }

            yield return (text[start..i].ToUpperInvariant(), start);
        }
    }

    /// <summary>Skips whitespace and comments from <paramref name="i"/>; returns the next code offset (or the length).</summary>
    private static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (StartsComment(text, i))
            {
                i = SkipToken(text, i);
            }
            else
            {
                break;
            }
        }

        return i;
    }

    private static bool StartsComment(string text, int i) =>
        i + 1 < text.Length && ((text[i] == '-' && text[i + 1] == '-') || (text[i] == '/' && text[i + 1] == '*'));

    /// <summary>Returns the offset just past the token starting at <paramref name="i"/> (a quote, comment or single character).</summary>
    private static int SkipToken(string sql, int i)
    {
        var ch = sql[i];
        if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
        {
            var newline = sql.IndexOf('\n', i + 2);
            return newline < 0 ? sql.Length : newline + 1;
        }

        if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
        {
            var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return end < 0 ? sql.Length : end + 2;
        }

        // A doubled closing quote ('it''s') just re-enters the quote on the next pass: same result.
        var close = ch switch
        {
            '\'' or '"' or '`' => ch,
            '[' => ']',
            _ => '\0',
        };
        if (close == '\0')
        {
            return i + 1;
        }

        var closing = sql.IndexOf(close, i + 1);
        return closing < 0 ? sql.Length : closing + 1;
    }

    private static bool IsWordStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static bool IsWordPart(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '$';
}
