using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Components;

public enum BodyKind
{
    Json,
    Text,
    Binary,
}

/// <summary>How a body is shown: pretty JSON, UTF-8 text, or a hex preview (Truncated when the body is longer than it).</summary>
public sealed record BodyView(BodyKind Kind, string Text, bool Truncated);

/// <summary>
/// The pure pieces of the Get messages page (spec §7 1f): the death summary chip, which death is
/// "the first one", how a body renders, and how a got message becomes a PublishRequest again for
/// requeue/republish.
/// </summary>
public static class MessageFormat
{
    public const int HexPreviewBytes = 256;
    private const int HexBytesPerLine = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonWriterOptions PrettyJson = new()
    {
        Indented = true,
        // Display only (Blazor HTML-encodes the text node) -- keep non-ASCII readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The row chip: the top x-death entry (RabbitMQ keeps the most recent death first), as
    /// "rejected ×5", or just the reason when it happened once. Null when the message never died.
    /// </summary>
    public static string? DeathSummary(RabbitMessage message) =>
        message.Deaths.Count == 0 ? null
            : message.Deaths[0].Count > 1 ? $"{message.Deaths[0].Reason} ×{message.Deaths[0].Count.ToString(CultureInfo.InvariantCulture)}"
            : message.Deaths[0].Reason;

    /// <summary>The x-death count property: every death record's count summed.</summary>
    public static long DeathCount(RabbitMessage message) => message.Deaths.Sum(d => d.Count);

    /// <summary>
    /// The death that matches x-first-death-queue/-exchange -- where the message originally died --
    /// else the oldest record (x-death is newest first). Null when the message never died.
    /// </summary>
    public static DeathRecord? FirstDeath(RabbitMessage message)
    {
        if (message.Deaths.Count == 0)
        {
            return null;
        }

        var hasFirstDeathHeaders = message.FirstDeathQueue is not null || message.FirstDeathExchange is not null;
        var matching = hasFirstDeathHeaders
            ? message.Deaths.FirstOrDefault(d =>
                (message.FirstDeathQueue is null || d.Queue == message.FirstDeathQueue)
                && (message.FirstDeathExchange is null || d.Exchange == message.FirstDeathExchange))
            : null;
        return matching ?? message.Deaths[^1];
    }

    /// <summary>The routing key the message was originally published with: the first death's first key, else its current one.</summary>
    public static string OriginalRoutingKey(RabbitMessage message) =>
        FirstDeath(message)?.RoutingKeys.FirstOrDefault() ?? message.RoutingKey;

    /// <summary>Where "Republish" sends a message by default: back to the exchange it first died from, else where it came from.</summary>
    public static string OriginalExchange(RabbitMessage message) => message.FirstDeathExchange ?? message.Exchange;

    public static string DeliveryMode(RabbitMessage message) => message.IsPersistent ? "2 · persistent" : "1 · transient";

    public static BodyView RenderBody(byte[] body)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return Hex(body);
        }

        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')))
        {
            return Hex(body);
        }

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, PrettyJson))
                {
                    document.RootElement.WriteTo(writer);
                }

                return new BodyView(BodyKind.Json, Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n"), false);
            }
            catch (JsonException)
            {
                // Not JSON after all -- fall through to text.
            }
        }

        return new BodyView(BodyKind.Text, text, false);
    }

    private static BodyView Hex(byte[] body)
    {
        var shown = Math.Min(body.Length, HexPreviewBytes);
        var lines = new List<string>();
        for (var offset = 0; offset < shown; offset += HexBytesPerLine)
        {
            var bytes = body.Skip(offset).Take(Math.Min(HexBytesPerLine, shown - offset)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture));
            lines.Add($"{offset.ToString("x4", CultureInfo.InvariantCulture)}  {string.Join(' ', bytes)}");
        }

        return new BodyView(BodyKind.Binary, string.Join('\n', lines), body.Length > HexPreviewBytes);
    }

    /// <summary>A got message as a publish, carrying its body, identity, headers and persistence.</summary>
    public static PublishRequest ToPublishRequest(RabbitMessage message, string exchange, string routingKey) =>
        new(
            exchange,
            routingKey,
            message.Body,
            Persistent: message.IsPersistent,
            Priority: message.Priority,
            ContentType: message.ContentType,
            Headers: message.Headers,
            MessageId: message.MessageId,
            CorrelationId: message.CorrelationId,
            Timestamp: message.Timestamp,
            AppId: message.AppId);
}
