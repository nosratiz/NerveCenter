using System.Text.Json.Serialization;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>What a completion item is; serialised by name so postgres-completion.js can pick a badge.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PgCompletionKind>))]
public enum PgCompletionKind
{
    Keyword,
    Function,
    Schema,
    Table,
    View,
    Column,
    Alias,
}

/// <summary>One suggestion: Label is shown and filtered on, InsertText replaces the range (quoted / qualified / upper-cased as needed), Detail is the muted hint.</summary>
public sealed record PgCompletionItem(string Label, string InsertText, PgCompletionKind Kind, string? Detail);

/// <summary>Accepting an item replaces text[ReplaceFrom..ReplaceTo) with its InsertText (UTF-16 offsets, as the textarea counts).</summary>
public sealed record PgCompletionResult(int ReplaceFrom, int ReplaceTo, IReadOnlyList<PgCompletionItem> Items);
