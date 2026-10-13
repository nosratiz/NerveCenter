using System.Text.Json.Serialization;

namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>What a completion item is; serialised by name so the editor's JS can pick a badge.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CompletionKind>))]
public enum CompletionKind
{
    Keyword,
    Function,
    Table,
    View,
    Column,
    Alias,
}

/// <summary>One suggestion: Label is shown, InsertText replaces the range (quoted / upper-cased as needed), Detail is the muted hint.</summary>
public sealed record CompletionItem(string Label, string InsertText, CompletionKind Kind, string? Detail);

/// <summary>Accepting an item replaces text[ReplaceFrom..ReplaceTo) with its InsertText (UTF-16 offsets, as the textarea counts).</summary>
public sealed record CompletionResult(int ReplaceFrom, int ReplaceTo, IReadOnlyList<CompletionItem> Items);
