namespace SbConsole.Plugins.Postgres.Client;

public sealed record PgStatement(int Start, int Length, string Text, string FirstKeyword)
{
    /// <summary>The statement text with leading whitespace and comments removed.</summary>
    public string Body => PgSqlLexer.StripLeadingTrivia(Text);
}

/// <summary>
/// Splits a SQL script on top-level semicolons, honouring every PostgreSQL quoting form.
/// Must never see fewer statements than the server; over-splitting only refuses.
/// </summary>
public static class PgSqlLexer
{
    public static IReadOnlyList<PgStatement> Split(string sql)
    {
        var result = new List<PgStatement>();
        var start = 0;
        var i = 0;
        while (i < sql.Length)
        {
            if (sql[i] == ';')
            {
                Add(sql, start, i, result);
                i++;
                start = i;
                continue;
            }

            i = SkipToken(sql, i);
        }

        Add(sql, start, sql.Length, result);
        return result;
    }

    public static (int Line, int Column) LineAndColumn(string sql, int position)
    {
        var index = Math.Clamp(position - 1, 0, sql.Length);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (sql[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, index - lineStart + 1);
    }

    private static void Add(string sql, int start, int end, List<PgStatement> result)
    {
        var raw = sql.AsSpan(start, end - start);
        var lead = raw.Length - raw.TrimStart().Length;
        var text = raw.Trim().ToString();
        if (text.Length == 0 || FirstKeyword(text) is "" && IsOnlyTrivia(text))
        {
            return;
        }

        result.Add(new PgStatement(start + lead, text.Length, text, FirstKeyword(text)));
    }

    private static bool IsOnlyTrivia(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var next = SkipTrivia(text, i);
            if (next == i)
            {
                return false;
            }

            i = next;
        }

        return true;
    }

    internal static string StripLeadingTrivia(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var next = SkipTrivia(text, i);
            if (next == i)
            {
                break;
            }

            i = next;
        }

        return text[i..];
    }

    private static string FirstKeyword(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var next = SkipTrivia(text, i);
            if (next != i)
            {
                i = next;
                continue;
            }

            if (char.IsAsciiLetter(text[i]) || text[i] == '_')
            {
                var j = i;
                while (j < text.Length && (char.IsAsciiLetter(text[j]) || text[j] == '_'))
                {
                    j++;
                }

                return text[i..j].ToUpperInvariant();
            }

            return "";
        }

        return "";
    }

    /// <summary>Skips whitespace or one comment starting at i; returns i if neither.</summary>
    private static int SkipTrivia(string s, int i)
    {
        if (IsPgWhitespace(s[i]))
        {
            return i + 1;
        }

        if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-')
        {
            var nl = s.AsSpan(i).IndexOfAny('\n', '\r');
            return nl < 0 ? s.Length : i + nl + 1;
        }

        if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
        {
            return SkipBlockComment(s, i);
        }

        return i;
    }

    private static int SkipBlockComment(string s, int i)
    {
        var depth = 0;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }

        return s.Length;
    }

    /// <summary>Advances over one token (a quoted section, comment, or single char) and returns the new index.</summary>
    private static int SkipToken(string s, int i)
    {
        var trivia = SkipTrivia(s, i);
        if (trivia != i)
        {
            return trivia;
        }

        var c = s[i];
        if ((c == 'E' || c == 'e') && i + 1 < s.Length && s[i + 1] == '\'' && !IsIdentChar(i > 0 ? s[i - 1] : ' '))
        {
            return SkipQuoted(s, i + 1, '\'', backslash: true);
        }

        if (c == '\'' || c == '"')
        {
            return SkipQuoted(s, i, c, backslash: false);
        }

        if (c == '$')
        {
            var tagEnd = TagEnd(s, i);
            if (tagEnd > 0)
            {
                var tag = s.Substring(i, tagEnd - i + 1);
                var close = s.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                return close < 0 ? s.Length : close + tag.Length;
            }
        }

        return i + 1;
    }

    private static bool IsPgWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$' || c >= 0x80;

    private static bool IsTagStart(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

    /// <summary>Index of the closing '$' of a dollar-quote opener at i, or -1 (e.g. for the parameter $1).</summary>
    private static int TagEnd(string s, int i)
    {
        if (i > 0 && IsIdentChar(s[i - 1]))
        {
            return -1;
        }

        var j = i + 1;
        if (j < s.Length && IsTagStart(s[j]))
        {
            while (j < s.Length && (IsTagStart(s[j]) || char.IsAsciiDigit(s[j])))
            {
                j++;
            }
        }

        return j < s.Length && s[j] == '$' ? j : -1;
    }

    private static int SkipQuoted(string s, int i, char quote, bool backslash)
    {
        var j = i + 1;
        while (j < s.Length)
        {
            if (backslash && s[j] == '\\')
            {
                j += 2;
                continue;
            }

            if (s[j] == quote)
            {
                if (j + 1 < s.Length && s[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return s.Length;
    }
}
