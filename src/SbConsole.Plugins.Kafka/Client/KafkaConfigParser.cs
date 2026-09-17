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
