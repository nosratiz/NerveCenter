namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Parses a connection secret shaped as a librdkafka config string ("key=value" pairs separated
/// by ";", e.g. "bootstrap.servers=broker1:9092;security.protocol=SASL_SSL;sasl.mechanism=PLAIN")
/// into the dictionary every Confluent.Kafka ClientConfig-derived type wraps directly -- see the
/// design spec (docs/superpowers/specs/2026-09-16-kafka-topics-plugin-design.md) §2. A malformed
/// segment (no "=") is skipped rather than throwing: the resulting config simply won't have that
/// key, which every caller already surfaces as a friendly connection-shaped error rather than a
/// parser exception.
/// </summary>
public static class KafkaConfigParser
{
    private static readonly string[] SafeEchoKeys = ["bootstrap.servers", "security.protocol", "sasl.mechanism"];

    /// <summary>
    /// A small allowlist-only echo of non-secret connection fields, safe to render in the UI
    /// under the cluster picker so a user can confirm which cluster/security mode they're
    /// connected to. Deliberately excludes every credential-shaped key (sasl.username,
    /// sasl.password, ssl.key.password, ssl.keystore.password, etc.) so the decrypted secret's
    /// credentials never reach the browser, even indirectly. Keys are echoed in the fixed order
    /// above (not the input string's order), joined by " · "; a config with none of these keys
    /// present returns "".
    /// </summary>
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
            var value = segment[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }
}
