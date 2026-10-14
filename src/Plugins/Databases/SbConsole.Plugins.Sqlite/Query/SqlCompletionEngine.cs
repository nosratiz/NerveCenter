using SbConsole.Plugins.Sqlite.Client;
using SQLitePCL;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>
/// Query editor completion (design spec §12). Pure: (text, caret, schema) → the range to replace and
/// up to MaxItems suggestions. A small tokenizer with the same quote/comment rules as SqliteScript
/// finds the current statement (a ';' ends one only when sqlite3_complete agrees, so a trigger body
/// stays whole), then the context decides what is offered:
/// <list type="bullet">
/// <item>inside a string, quoted name or comment → nothing;</item>
/// <item>after <c>qualifier.</c> → that table's/view's columns (aliases from FROM/JOIN/UPDATE/INTO; <c>main.</c> → tables);</item>
/// <item>after FROM, JOIN, INTO, UPDATE, TABLE, a FROM-list comma, or CREATE INDEX … ON → tables and views;</item>
/// <item>otherwise → the statement's tables' columns, its aliases, keywords, functions, tables and views.</item>
/// </list>
/// The identifier before the caret filters: prefix matches first, then substring matches, both
/// case-insensitive, kind order kept within each group.
/// </summary>
public static class SqlCompletionEngine
{
    public const int MaxItems = 50;

    private static readonly HashSet<string> TableKeywords = new(["FROM", "JOIN", "INTO", "UPDATE", "TABLE"], StringComparer.OrdinalIgnoreCase);

    // Words that open a clause: scanning back from a ',' to the nearest one tells a FROM list from a SELECT list.
    private static readonly HashSet<string> ClauseKeywords = new(
        ["SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "SET", "VALUES", "ON", "USING", "JOIN", "RETURNING", "WINDOW", "BY"],
        StringComparer.OrdinalIgnoreCase);

    static SqlCompletionEngine() => Batteries_V2.Init(); // sqlite3_complete, as in SqliteScript

    private enum TokenKind
    {
        Word,
        Quoted,
        String,
        Comment,
        Number,
        Punct,
    }

    private readonly record struct Token(TokenKind Kind, int Start, int End, bool Terminated);

    private sealed record TableRef(CompletionObject Object, string? Alias);

    public static CompletionResult Complete(string text, int caret, CompletionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schema);
        caret = Math.Clamp(caret, 0, text.Length);
        var none = new CompletionResult(caret, caret, []);
        var tokens = Tokenize(text);

        // Inside a string, quoted name or comment: an unterminated one runs to the end of the text.
        foreach (var t in tokens)
        {
            if (t.Kind is TokenKind.String or TokenKind.Quoted or TokenKind.Comment
                && t.Start < caret && (caret < t.End || (caret == t.End && !t.Terminated)))
            {
                return none;
            }
        }

        var statement = CurrentStatement(text, tokens, caret);
        var replaceFrom = caret;
        var replaceTo = caret;
        foreach (var t in statement)
        {
            if (t.Start < caret && caret <= t.End)
            {
                if (t.Kind == TokenKind.Number)
                {
                    return none;
                }

                if (t.Kind == TokenKind.Word)
                {
                    replaceFrom = t.Start;
                    replaceTo = t.End;
                }
            }
        }

        var prefix = text[replaceFrom..caret];
        var code = statement.Where(t => t.Kind != TokenKind.Comment).ToList();
        var before = code.Where(t => t.End <= replaceFrom).ToList();
        var objects = schema.Objects.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var refs = TableRefs(text, code, objects);

        var candidates = Candidates(text, before, code, refs, objects, schema);
        if (candidates is null)
        {
            return new CompletionResult(replaceFrom, replaceTo, []);
        }

        // Accepting an item identical to the word just typed would change nothing, and would eat the
        // Enter meant as a newline -- leave it out.
        var typed = caret == replaceTo ? text[replaceFrom..replaceTo] : null;
        var items = candidates
            .Select(item => (Item: item, Rank: Rank(item.Label, prefix)))
            .Where(x => x.Rank >= 0 && x.Item.InsertText != typed)
            .OrderBy(x => x.Rank)
            .Take(MaxItems)
            .Select(x => x.Item)
            .ToList();
        return new CompletionResult(replaceFrom, replaceTo, items);
    }

    /// <summary>The ordered candidates for the context, or null when nothing applies (an unknown qualifier).</summary>
    private static List<CompletionItem>? Candidates(
        string text, List<Token> before, List<Token> code, List<TableRef> refs,
        Dictionary<string, CompletionObject> objects, CompletionSchema schema)
    {
        var previous = before.Count > 0 ? before[^1] : (Token?)null;

        if (previous is { Kind: TokenKind.Punct } dot && text[dot.Start] == '.')
        {
            if (before.Count < 2 || Name(text, before[^2]) is not { } qualifier)
            {
                return null;
            }

            var target = refs.FirstOrDefault(r => string.Equals(r.Alias, qualifier, StringComparison.OrdinalIgnoreCase))?.Object
                ?? objects.GetValueOrDefault(qualifier);
            if (target is not null)
            {
                return [.. ColumnItems(target)];
            }

            return qualifier.Equals("main", StringComparison.OrdinalIgnoreCase) || qualifier.Equals("temp", StringComparison.OrdinalIgnoreCase)
                ? [.. TableItems(schema)]
                : null;
        }

        if (previous is { } prev && IsTablePosition(text, prev, before, code))
        {
            return [.. TableItems(schema)];
        }

        var items = new List<CompletionItem>();
        foreach (var obj in refs.Select(r => r.Object).Distinct())
        {
            items.AddRange(ColumnItems(obj));
        }

        items.AddRange(refs.Where(r => r.Alias is not null)
            .DistinctBy(r => r.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(r => new CompletionItem(r.Alias!, r.Alias!, CompletionKind.Alias, r.Object.Name)));
        items.AddRange(SqlCompletionVocabulary.Keywords.Select(k => new CompletionItem(k, k, CompletionKind.Keyword, null)));
        items.AddRange(SqlCompletionVocabulary.Functions.Select(f =>
            new CompletionItem(f.Name, f.Signature.EndsWith("()", StringComparison.Ordinal) ? f.Name + "()" : f.Name + "(", CompletionKind.Function, f.Signature)));
        items.AddRange(TableItems(schema));
        return items;
    }

    private static bool IsTablePosition(string text, Token previous, List<Token> before, List<Token> code)
    {
        if (previous.Kind == TokenKind.Word)
        {
            var word = Slice(text, previous);
            if (TableKeywords.Contains(word))
            {
                return true;
            }

            // CREATE [UNIQUE] INDEX … ON <table>, never a join's ON.
            return word.Equals("ON", StringComparison.OrdinalIgnoreCase)
                && code.Count > 0 && Slice(text, code[0]).Equals("CREATE", StringComparison.OrdinalIgnoreCase)
                && before.Any(t => t.Kind == TokenKind.Word && Slice(text, t).Equals("INDEX", StringComparison.OrdinalIgnoreCase));
        }

        if (previous.Kind == TokenKind.Punct && text[previous.Start] == ',')
        {
            var depth = 0;
            for (var i = before.Count - 2; i >= 0; i--)
            {
                var t = before[i];
                if (t.Kind == TokenKind.Punct)
                {
                    depth += text[t.Start] switch { ')' => 1, '(' => -1, _ => 0 };
                    if (depth < 0)
                    {
                        return false;
                    }
                }
                else if (depth == 0 && t.Kind == TokenKind.Word && ClauseKeywords.Contains(Slice(text, t)))
                {
                    return Slice(text, t).Equals("FROM", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return false;
    }

    /// <summary>Tables named after FROM / JOIN / UPDATE / INTO (and further FROM-list entries) that exist in the schema, with any alias.</summary>
    private static List<TableRef> TableRefs(string text, List<Token> code, Dictionary<string, CompletionObject> objects)
    {
        var refs = new List<TableRef>();
        for (var i = 0; i < code.Count; i++)
        {
            var keyword = code[i].Kind == TokenKind.Word ? Slice(text, code[i]).ToUpperInvariant() : "";
            if (keyword is not ("FROM" or "JOIN" or "UPDATE" or "INTO"))
            {
                continue;
            }

            var j = i + 1;
            // UPDATE OR REPLACE x
            if (keyword == "UPDATE" && j + 1 < code.Count && IsWord(text, code[j], "OR"))
            {
                j += 2;
            }

            while (j < code.Count)
            {
                var next = ReadRef(text, code, j, objects, refs);
                if (next < 0 || keyword != "FROM" || next >= code.Count || !IsPunct(text, code[next], ','))
                {
                    break;
                }

                j = next + 1;
            }
        }

        return refs;
    }

    /// <summary>Reads <c>[schema.]name [[AS] alias]</c> at <paramref name="j"/>; returns the index after it, or -1 when no name is there.</summary>
    private static int ReadRef(string text, List<Token> code, int j, Dictionary<string, CompletionObject> objects, List<TableRef> refs)
    {
        if (Name(text, code[j]) is not { } name)
        {
            return -1;
        }

        if (j + 2 < code.Count && IsPunct(text, code[j + 1], '.') && Name(text, code[j + 2]) is { } qualified)
        {
            name = qualified;
            j += 2;
        }

        j++;
        string? alias = null;
        var hasAs = j < code.Count && IsWord(text, code[j], "AS");
        var k = hasAs ? j + 1 : j;
        if (k < code.Count
            && (code[k].Kind == TokenKind.Quoted || (code[k].Kind == TokenKind.Word && !SqlCompletionVocabulary.KeywordSet.Contains(Slice(text, code[k])))))
        {
            alias = Name(text, code[k]);
            j = k + 1;
        }

        if (objects.TryGetValue(name, out var obj))
        {
            refs.Add(new TableRef(obj, alias));
        }

        return j;
    }

    private static IEnumerable<CompletionItem> ColumnItems(CompletionObject obj) =>
        obj.Columns.Select(c =>
        {
            var type = (c.DeclaredType + (c.PrimaryKey ? " PK" : "")).Trim();
            return new CompletionItem(c.Name, Identifier(c.Name), CompletionKind.Column, type.Length > 0 ? $"{type} · {obj.Name}" : obj.Name);
        });

    private static IEnumerable<CompletionItem> TableItems(CompletionSchema schema) =>
        schema.Objects.Select(o =>
        {
            var view = o.Type == SchemaObjectType.View;
            var columns = o.Columns.Count == 1 ? "1 column" : $"{o.Columns.Count} columns";
            return new CompletionItem(o.Name, Identifier(o.Name), view ? CompletionKind.View : CompletionKind.Table, $"{(view ? "view" : "table")} · {columns}");
        });

    /// <summary>A bare identifier when SQLite would read it as one, else quoted (SqliteSql.QuoteIdentifier).</summary>
    private static string Identifier(string name) =>
        name.Length > 0 && IsAsciiWordStart(name[0]) && name.All(ch => IsAsciiWordStart(ch) || char.IsAsciiDigit(ch) || ch == '$')
            && !SqlCompletionVocabulary.KeywordSet.Contains(name)
            ? name
            : SqliteSql.QuoteIdentifier(name);

    private static int Rank(string label, string prefix) =>
        prefix.Length == 0 || label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0
        : label.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1
        : -1;

    /// <summary>The tokens of the statement the caret is in; a ';' ends a statement only when sqlite3_complete agrees.</summary>
    private static List<Token> CurrentStatement(string text, List<Token> tokens, int caret)
    {
        var start = 0;
        var startIndex = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Punct || text[t.Start] != ';' || raw.sqlite3_complete(text[start..t.End]) == 0)
            {
                continue;
            }

            if (caret <= t.Start)
            {
                return tokens[startIndex..i];
            }

            start = t.End;
            startIndex = i + 1;
        }

        return tokens[startIndex..];
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            var start = i;
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (ch == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var newline = text.IndexOf('\n', i + 2);
                tokens.Add(newline < 0 ? new(TokenKind.Comment, start, text.Length, false) : new(TokenKind.Comment, start, newline + 1, true));
                i = tokens[^1].End;
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                tokens.Add(end < 0 ? new(TokenKind.Comment, start, text.Length, false) : new(TokenKind.Comment, start, end + 2, true));
                i = tokens[^1].End;
            }
            else if (ch is '\'' or '"' or '`' or '[')
            {
                tokens.Add(Quote(text, i));
                i = tokens[^1].End;
            }
            else if (char.IsAsciiDigit(ch) || (ch == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    i++;
                }

                tokens.Add(new(TokenKind.Number, start, i, true));
            }
            else if (IsWordStart(ch))
            {
                while (i < text.Length && IsWordPart(text[i]))
                {
                    i++;
                }

                tokens.Add(new(TokenKind.Word, start, i, true));
            }
            else
            {
                tokens.Add(new(TokenKind.Punct, start, i + 1, true));
                i++;
            }
        }

        return tokens;
    }

    /// <summary>'string', "name", `name` (a doubled quote escapes) or [name] (no escape).</summary>
    private static Token Quote(string text, int start)
    {
        var open = text[start];
        var close = open == '[' ? ']' : open;
        var kind = open == '\'' ? TokenKind.String : TokenKind.Quoted;
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == close)
            {
                if (close != ']' && i + 1 < text.Length && text[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                return new Token(kind, start, i + 1, true);
            }

            i++;
        }

        return new Token(kind, start, text.Length, false);
    }

    /// <summary>The identifier a Word or terminated quoted name stands for (unquoted); null for anything else.</summary>
    private static string? Name(string text, Token t)
    {
        if (t.Kind == TokenKind.Word)
        {
            return Slice(text, t);
        }

        if (t.Kind != TokenKind.Quoted || !t.Terminated || t.End - t.Start < 2)
        {
            return null;
        }

        var open = text[t.Start];
        var inner = text[(t.Start + 1)..(t.End - 1)];
        return open == '[' ? inner : inner.Replace(new string(open, 2), open.ToString(), StringComparison.Ordinal);
    }

    private static string Slice(string text, Token t) => text[t.Start..t.End];

    private static bool IsWord(string text, Token t, string word) =>
        t.Kind == TokenKind.Word && Slice(text, t).Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsPunct(string text, Token t, char ch) => t.Kind == TokenKind.Punct && text[t.Start] == ch;

    private static bool IsAsciiWordStart(char ch) => char.IsAsciiLetter(ch) || ch == '_';

    private static bool IsWordStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static bool IsWordPart(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '$';
}
