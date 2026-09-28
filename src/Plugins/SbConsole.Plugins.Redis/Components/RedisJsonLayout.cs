using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SbConsole.Plugins.Redis.Components;

/// <summary>What a piece of laid-out JSON is, so the string editor can tint keys and scalars (mockup 1c).</summary>
public enum JsonTokenKind { Punctuation, Key, String, Scalar }

public sealed record JsonToken(string Text, JsonTokenKind Kind);

/// <summary>
/// Pretty-prints a JSON value the way the mockup's string panel does: two-space indentation, but a
/// nested object/array whose compact form is short stays on one line (<c>{ "min": 0, "pct": 0 }</c>)
/// instead of spending five lines on it. Returns null when the text isn't valid JSON.
/// </summary>
public static class RedisJsonLayout
{
    private const int InlineMaxChars = 44;

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyList<JsonToken>? Tokenize(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var tokens = new List<JsonToken>();
            Write(doc.RootElement, 0, tokens);
            return tokens;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Format(string raw) =>
        Tokenize(raw) is { } tokens ? string.Concat(tokens.Select(t => t.Text)) : null;

    private static void Write(JsonElement element, int depth, List<JsonToken> tokens)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                if (depth > 0 && InlineLength(element) is { } length && length <= InlineMaxChars)
                {
                    WriteInline(element, tokens);
                }
                else
                {
                    WriteBlock(element, depth, tokens);
                }

                break;
            case JsonValueKind.String:
                tokens.Add(new JsonToken(Quote(element.GetString()!), JsonTokenKind.String));
                break;
            default:
                tokens.Add(new JsonToken(element.GetRawText(), JsonTokenKind.Scalar));
                break;
        }
    }

    private static void WriteBlock(JsonElement element, int depth, List<JsonToken> tokens)
    {
        var isObject = element.ValueKind == JsonValueKind.Object;
        var items = isObject
            ? element.EnumerateObject().Select(p => (Name: (string?)p.Name, p.Value)).ToList()
            : element.EnumerateArray().Select(v => (Name: (string?)null, Value: v)).ToList();
        if (items.Count == 0)
        {
            tokens.Add(new JsonToken(isObject ? "{}" : "[]", JsonTokenKind.Punctuation));
            return;
        }

        var inner = new string(' ', (depth + 1) * 2);
        tokens.Add(new JsonToken(isObject ? "{\n" : "[\n", JsonTokenKind.Punctuation));
        for (var i = 0; i < items.Count; i++)
        {
            tokens.Add(new JsonToken(inner, JsonTokenKind.Punctuation));
            if (items[i].Name is { } name)
            {
                tokens.Add(new JsonToken(Quote(name), JsonTokenKind.Key));
                tokens.Add(new JsonToken(": ", JsonTokenKind.Punctuation));
            }

            Write(items[i].Value, depth + 1, tokens);
            tokens.Add(new JsonToken(i < items.Count - 1 ? ",\n" : "\n", JsonTokenKind.Punctuation));
        }

        tokens.Add(new JsonToken(new string(' ', depth * 2) + (isObject ? "}" : "]"), JsonTokenKind.Punctuation));
    }

    private static void WriteInline(JsonElement element, List<JsonToken> tokens)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                tokens.Add(new JsonToken(properties.Count == 0 ? "{" : "{ ", JsonTokenKind.Punctuation));
                for (var i = 0; i < properties.Count; i++)
                {
                    tokens.Add(new JsonToken(Quote(properties[i].Name), JsonTokenKind.Key));
                    tokens.Add(new JsonToken(": ", JsonTokenKind.Punctuation));
                    WriteInline(properties[i].Value, tokens);
                    if (i < properties.Count - 1)
                    {
                        tokens.Add(new JsonToken(", ", JsonTokenKind.Punctuation));
                    }
                }

                tokens.Add(new JsonToken(properties.Count == 0 ? "}" : " }", JsonTokenKind.Punctuation));
                break;
            case JsonValueKind.Array:
                var values = element.EnumerateArray().ToList();
                tokens.Add(new JsonToken(values.Count == 0 ? "[" : "[ ", JsonTokenKind.Punctuation));
                for (var i = 0; i < values.Count; i++)
                {
                    WriteInline(values[i], tokens);
                    if (i < values.Count - 1)
                    {
                        tokens.Add(new JsonToken(", ", JsonTokenKind.Punctuation));
                    }
                }

                tokens.Add(new JsonToken(values.Count == 0 ? "]" : " ]", JsonTokenKind.Punctuation));
                break;
            default:
                Write(element, 1, tokens);
                break;
        }
    }

    /// <summary>Length of the one-line form, or null as soon as it's clearly too long (so a huge array isn't measured in full).</summary>
    private static int? InlineLength(JsonElement element)
    {
        var tokens = new List<JsonToken>();
        var raw = element.GetRawText();
        if (raw.Length > InlineMaxChars * 3)
        {
            return null;
        }

        WriteInline(element, tokens);
        var text = new StringBuilder();
        foreach (var token in tokens)
        {
            text.Append(token.Text);
        }

        return text.Length;
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, Relaxed);
}
