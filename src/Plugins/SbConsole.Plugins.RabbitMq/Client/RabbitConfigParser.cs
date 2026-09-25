namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// Parses/serializes the connection secret: a flat "key=value" string separated by ";" (design
/// spec docs/superpowers/specs/2026-09-25-rabbitmq-plugin-design.md §2), same shape as
/// AwsConfigParser with one difference: every value is percent-encoded. A RabbitMQ password or
/// vhost can legally contain ";" or "=", which would otherwise split the string. A malformed
/// segment (no "=") is skipped rather than throwing; RabbitConnectionSettings.From reports any
/// resulting missing required key as a readable error.
/// </summary>
public static class RabbitConfigParser
{
    // Allowlist, never a denylist: "password" (and any future credential-shaped key) can never
    // reach the browser through the echo.
    private static readonly string[] SafeEchoKeys = ["host", "amqpPort", "managementUrl", "vhost", "username"];

    // Fixed order for stable, human-diffable output; Parse itself is order-independent. Keys
    // outside this list are dropped, matching Parse's "skip, don't throw" tolerance.
    private static readonly string[] SerializeKeyOrder =
    [
        "host", "amqpPort", "managementUrl", "vhost", "username", "password", "tls", "verifyCert",
    ];

    public static string SafeEcho(string config)
    {
        var parsed = Parse(config);
        return string.Join(" · ", SafeEchoKeys.Where(parsed.ContainsKey).Select(k => $"{k}={parsed[k]}"));
    }

    public static Dictionary<string, string> Parse(string config)
    {
        var result = new Dictionary<string, string>();
        foreach (var segment in config.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            result[key] = Unescape(segment[(separatorIndex + 1)..].Trim());
        }

        return result;
    }

    public static string Serialize(IReadOnlyDictionary<string, string> fields)
    {
        var parts = new List<string>();
        foreach (var key in SerializeKeyOrder)
        {
            if (fields.TryGetValue(key, out var value) && value.Length > 0)
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        return string.Join(';', parts);
    }

    // Uri.UnescapeDataString already leaves an invalid "%zz" sequence untouched; the catch is a
    // belt-and-braces guard so a hand-edited secret can never make Parse throw.
    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
