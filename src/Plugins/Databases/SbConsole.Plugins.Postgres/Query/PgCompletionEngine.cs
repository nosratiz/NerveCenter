using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>
/// Query editor completion (design spec §16). Pure: (text, caret, schema) → the range to replace and
/// up to MaxItems suggestions. A small tokenizer with PgSqlLexer's quoting rules ('…' with '' escapes,
/// E'…' with backslash escapes, $tag$…$tag$, "…" names, -- and nested /* */ comments) finds the
/// statement holding the caret (split on top-level ';'), then the context decides what is offered:
/// <list type="bullet">
/// <item>inside a string, dollar-quoted body, quoted name or comment, in a number, or right after <c>::</c> → nothing;</item>
/// <item>after <c>s.t.</c> → that table's columns; after <c>q.</c> → the columns of the alias/table q names, else the
/// tables of schema q, else the columns of q resolved through the search path;</item>
/// <item>after FROM, JOIN, INTO, UPDATE, TABLE, ONLY, a FROM-list comma, or CREATE INDEX … ON → tables/views, then schemas;</item>
/// <item>otherwise → the statement's tables' columns, its aliases, keywords, functions, tables/views, schemas.</item>
/// </list>
/// Unquoted names match case-insensitively, quoted ones exactly. The identifier before the caret
/// filters: prefix matches first, then substring matches, both case-insensitive, kind order kept.
/// </summary>
public static class PgCompletionEngine
{
    public const int MaxItems = 50;

    private static readonly HashSet<string> TableKeywords = new(["FROM", "JOIN", "INTO", "UPDATE", "TABLE", "ONLY"], StringComparer.OrdinalIgnoreCase);

    // Words that open a clause: scanning back from a ',' to the nearest one tells a FROM list from a SELECT list.
    private static readonly HashSet<string> ClauseKeywords = new(
        ["SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "SET", "VALUES", "ON", "USING", "JOIN", "RETURNING", "WINDOW", "BY"],
        StringComparer.OrdinalIgnoreCase);

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

    /// <summary>A name as written: a quoted one matches exactly, a bare one case-insensitively.</summary>
    private readonly record struct Ident(string Value, bool Quoted)
    {
        public bool Matches(string actual) =>
            string.Equals(actual, Value, Quoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TableRef(PgCompletionObject Object, Ident? Alias);

    public static PgCompletionResult Complete(string text, int caret, PgCompletionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schema);
        caret = Math.Clamp(caret, 0, text.Length);
        var none = new PgCompletionResult(caret, caret, []);
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
        var refs = TableRefs(text, code, schema);

        var candidates = Candidates(text, before, code, refs, schema);
        if (candidates is null)
        {
            return new PgCompletionResult(replaceFrom, replaceTo, []);
        }

        // A finished word (caret at its end) that equals a candidate offers nothing: accepting would change
        // nothing, and the next prefix match (orders -> orders_archive, OR -> ORDER) would eat the Enter meant
        // as a newline. Typing on (orders_) shows the longer matches again.
        if (caret == replaceTo && replaceFrom < replaceTo)
        {
            var word = text[replaceFrom..replaceTo];
            if (candidates.Any(c => string.Equals(c.InsertText, word, StringComparison.OrdinalIgnoreCase)))
            {
                return new PgCompletionResult(replaceFrom, replaceTo, []);
            }
        }

        var items = candidates
            .Select(item => (Item: item, Rank: Rank(item.Label, prefix)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .Take(MaxItems)
            .Select(x => x.Item)
            .ToList();
        return new PgCompletionResult(replaceFrom, replaceTo, items);
    }

    /// <summary>The ordered candidates for the context, or null when nothing applies (an unknown qualifier).</summary>
    private static List<PgCompletionItem>? Candidates(
        string text, List<Token> before, List<Token> code, List<TableRef> refs, PgCompletionSchema schema)
    {
        var previous = before.Count > 0 ? before[^1] : (Token?)null;

        if (previous is { Kind: TokenKind.Punct } cast && Slice(text, cast) == "::")
        {
            return []; // a type name goes here; types are out of scope
        }

        if (previous is { Kind: TokenKind.Punct } dot && text[dot.Start] == '.')
        {
            return QualifiedCandidates(text, before, refs, schema);
        }

        if (previous is { } prev && IsTablePosition(text, prev, before, code))
        {
            return [.. TableItems(schema, schema.Objects, qualify: true), .. SchemaItems(schema)];
        }

        var items = new List<PgCompletionItem>();
        foreach (var obj in refs.Select(r => r.Object).Distinct())
        {
            items.AddRange(ColumnItems(obj));
        }

        items.AddRange(refs.Where(r => r.Alias is not null)
            .Select(r => (Alias: r.Alias!.Value, r.Object))
            .DistinctBy(r => (r.Alias.Quoted, Value: r.Alias.Quoted ? r.Alias.Value : r.Alias.Value.ToLowerInvariant()))
            .Select(r => new PgCompletionItem(
                r.Alias.Value, r.Alias.Quoted ? PgIdent.Quote(r.Alias.Value) : r.Alias.Value, PgCompletionKind.Alias, r.Object.Name)));
        items.AddRange(PgCompletionVocabulary.Keywords.Select(k => new PgCompletionItem(k, k, PgCompletionKind.Keyword, null)));
        items.AddRange(PgCompletionVocabulary.Functions.Select(f => new PgCompletionItem(
            f.Name, f.Signature.EndsWith("()", StringComparison.Ordinal) ? f.Name + "()" : f.Name + "(", PgCompletionKind.Function, f.Signature)));
        items.AddRange(TableItems(schema, schema.Objects, qualify: true));
        items.AddRange(SchemaItems(schema));
        return items;
    }

    /// <summary>Candidates after <c>q.</c> (before[^1] is the dot).</summary>
    private static List<PgCompletionItem>? QualifiedCandidates(string text, List<Token> before, List<TableRef> refs, PgCompletionSchema schema)
    {
        if (before.Count < 2 || Name(text, before[^2]) is not { } qualifier)
        {
            return null;
        }

        // schema.table.
        if (before.Count >= 4 && IsPunct(text, before[^3], '.') && Name(text, before[^4]) is { } schemaName)
        {
            var obj = schema.Objects.FirstOrDefault(o => schemaName.Matches(o.Schema) && qualifier.Matches(o.Name));
            return obj is null ? null : [.. ColumnItems(obj)];
        }

        // An alias wins over a table of the statement, which wins over a schema of the same name.
        var target = refs.FirstOrDefault(r => r.Alias is { } alias && qualifier.Matches(alias.Value))?.Object
            ?? refs.FirstOrDefault(r => qualifier.Matches(r.Object.Name))?.Object;
        if (target is not null)
        {
            return [.. ColumnItems(target)];
        }

        var schemaMatch = schema.Schemas.FirstOrDefault(s => qualifier.Matches(s));
        if (schemaMatch is not null)
        {
            return [.. TableItems(schema, schema.Objects.Where(o => o.Schema == schemaMatch), qualify: false)];
        }

        return Resolve(schema, qualifier) is { } resolved ? [.. ColumnItems(resolved)] : null;
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
                && code.Count > 0 && IsWord(text, code[0], "CREATE")
                && before.Any(t => IsWord(text, t, "INDEX"));
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
    private static List<TableRef> TableRefs(string text, List<Token> code, PgCompletionSchema schema)
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
            while (j < code.Count)
            {
                if (IsWord(text, code[j], "ONLY"))
                {
                    j++;
                    if (j >= code.Count)
                    {
                        break;
                    }
                }

                var next = ReadRef(text, code, j, schema, refs);
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
    private static int ReadRef(string text, List<Token> code, int j, PgCompletionSchema schema, List<TableRef> refs)
    {
        if (Name(text, code[j]) is not { } first)
        {
            return -1;
        }

        PgCompletionObject? obj;
        if (j + 2 < code.Count && IsPunct(text, code[j + 1], '.') && Name(text, code[j + 2]) is { } second)
        {
            obj = schema.Objects.FirstOrDefault(o => first.Matches(o.Schema) && second.Matches(o.Name));
            j += 2;
        }
        else
        {
            obj = Resolve(schema, first);
        }

        j++;
        Ident? alias = null;
        var k = j < code.Count && IsWord(text, code[j], "AS") ? j + 1 : j;
        if (k < code.Count
            && ((code[k].Kind == TokenKind.Quoted && code[k].Terminated)
                || (code[k].Kind == TokenKind.Word && !PgCompletionVocabulary.KeywordSet.Contains(Slice(text, code[k])))))
        {
            alias = Name(text, code[k]);
            j = k + 1;
        }

        if (obj is not null)
        {
            refs.Add(new TableRef(obj, alias));
        }

        return j;
    }

    /// <summary>An unqualified relation name: the first search-path schema (in path order) that has it.</summary>
    private static PgCompletionObject? Resolve(PgCompletionSchema schema, Ident name)
    {
        foreach (var path in schema.SearchPath)
        {
            var obj = schema.Objects.FirstOrDefault(o => o.Schema == path && name.Matches(o.Name));
            if (obj is not null)
            {
                return obj;
            }
        }

        return null;
    }

    private static IEnumerable<PgCompletionItem> ColumnItems(PgCompletionObject obj) =>
        obj.Columns.Select(c => new PgCompletionItem(
            c.Name, Identifier(c.Name), PgCompletionKind.Column, $"{c.Type}{(c.PrimaryKey ? " PK" : "")} · {obj.Name}"));

    /// <summary>qualify: a relation whose schema is not on the search path is shown and inserted as schema.name.</summary>
    private static IEnumerable<PgCompletionItem> TableItems(PgCompletionSchema schema, IEnumerable<PgCompletionObject> objects, bool qualify) =>
        objects.Select(o =>
        {
            var offPath = qualify && !schema.SearchPath.Contains(o.Schema);
            var label = offPath ? $"{o.Schema}.{o.Name}" : o.Name;
            var insert = offPath ? $"{Identifier(o.Schema)}.{Identifier(o.Name)}" : Identifier(o.Name);
            var view = o.Kind is PgObjectKind.View or PgObjectKind.MaterializedView;
            var columns = o.Columns.Count == 1 ? "1 column" : $"{o.Columns.Count} columns";
            return new PgCompletionItem(label, insert, view ? PgCompletionKind.View : PgCompletionKind.Table, $"{KindText(o.Kind)} · {o.Schema} · {columns}");
        });

    private static IEnumerable<PgCompletionItem> SchemaItems(PgCompletionSchema schema) =>
        schema.Schemas.Select(s => new PgCompletionItem(s, Identifier(s), PgCompletionKind.Schema, "schema"));

    private static string KindText(PgObjectKind kind) => kind switch
    {
        PgObjectKind.View => "view",
        PgObjectKind.MaterializedView => "materialized view",
        PgObjectKind.PartitionedTable => "partitioned table",
        PgObjectKind.ForeignTable => "foreign table",
        _ => "table",
    };

    /// <summary>Bare when PostgreSQL would read it back unchanged (lower-case, not reserved), else quoted.</summary>
    private static string Identifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetterLower(name[0]) || name[0] == '_')
            && name.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '_' or '$')
            && !PgCompletionVocabulary.ReservedSet.Contains(name)
            ? name
            : PgIdent.Quote(name);

    private static int Rank(string label, string prefix) =>
        prefix.Length == 0 || label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0
        : label.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1
        : -1;

    /// <summary>The tokens of the statement holding the caret; strings and comments are whole tokens, so every ';' token is top-level.</summary>
    private static List<Token> CurrentStatement(string text, List<Token> tokens, int caret)
    {
        var startIndex = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Punct || text[t.Start] != ';')
            {
                continue;
            }

            if (caret <= t.Start)
            {
                return tokens[startIndex..i];
            }

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
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            Token token;
            if (ch == '-' && Next(text, i) == '-')
            {
                var newline = text.IndexOf('\n', i + 2);
                token = newline < 0 ? new(TokenKind.Comment, i, text.Length, false) : new(TokenKind.Comment, i, newline + 1, true);
            }
            else if (ch == '/' && Next(text, i) == '*')
            {
                token = BlockComment(text, i);
            }
            else if (ch is 'E' or 'e' && Next(text, i) == '\'')
            {
                token = Quote(text, i, i + 1, TokenKind.String, backslash: true);
            }
            else if (ch == '\'')
            {
                token = Quote(text, i, i, TokenKind.String, backslash: false);
            }
            else if (ch == '"')
            {
                token = Quote(text, i, i, TokenKind.Quoted, backslash: false);
            }
            else if (ch == '$' && DollarTag(text, i) is { } tag)
            {
                var close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                token = close < 0 ? new(TokenKind.String, i, text.Length, false) : new(TokenKind.String, i, close + tag.Length, true);
            }
            else if (ch == ':' && Next(text, i) == ':')
            {
                token = new(TokenKind.Punct, i, i + 2, true);
            }
            else if (char.IsAsciiDigit(ch) || (ch == '.' && Next(text, i) is { } digit && char.IsAsciiDigit(digit)))
            {
                var j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '_' or '.'))
                {
                    j++;
                }

                token = new(TokenKind.Number, i, j, true);
            }
            else if (char.IsLetter(ch) || ch == '_')
            {
                var j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '_' or '$'))
                {
                    j++;
                }

                token = new(TokenKind.Word, i, j, true);
            }
            else
            {
                token = new(TokenKind.Punct, i, i + 1, true);
            }

            tokens.Add(token);
            i = token.End;
        }

        return tokens;
    }

    /// <summary>A quoted section starting at <paramref name="open"/> (a doubled quote escapes; with backslash, \x escapes too).</summary>
    private static Token Quote(string text, int start, int open, TokenKind kind, bool backslash)
    {
        var quote = text[open];
        var i = open + 1;
        while (i < text.Length)
        {
            if (backslash && text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
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

    /// <summary>/* … */ with nesting, as PostgreSQL reads it.</summary>
    private static Token BlockComment(string text, int start)
    {
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            if (text[i] == '/' && Next(text, i) == '*')
            {
                depth++;
                i += 2;
            }
            else if (text[i] == '*' && Next(text, i) == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return new Token(TokenKind.Comment, start, i, true);
                }
            }
            else
            {
                i++;
            }
        }

        return new Token(TokenKind.Comment, start, text.Length, false);
    }

    /// <summary>"$$" or "$tag$" at <paramref name="i"/> (tag: a letter or _ then letters, digits, _); null otherwise -- "$1" is a parameter.</summary>
    private static string? DollarTag(string text, int i)
    {
        var j = i + 1;
        if (j < text.Length && (char.IsLetter(text[j]) || text[j] == '_'))
        {
            while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
            {
                j++;
            }
        }

        return j < text.Length && text[j] == '$' ? text[i..(j + 1)] : null;
    }

    /// <summary>The name a Word or terminated quoted identifier stands for; null for anything else.</summary>
    private static Ident? Name(string text, Token t)
    {
        if (t.Kind == TokenKind.Word)
        {
            return new Ident(Slice(text, t), false);
        }

        if (t.Kind != TokenKind.Quoted || !t.Terminated || t.End - t.Start < 2)
        {
            return null;
        }

        return new Ident(text[(t.Start + 1)..(t.End - 1)].Replace("\"\"", "\"", StringComparison.Ordinal), true);
    }

    private static char? Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : null;

    private static string Slice(string text, Token t) => text[t.Start..t.End];

    private static bool IsWord(string text, Token t, string word) =>
        t.Kind == TokenKind.Word && Slice(text, t).Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsPunct(string text, Token t, char ch) => t.Kind == TokenKind.Punct && text[t.Start] == ch;
}
