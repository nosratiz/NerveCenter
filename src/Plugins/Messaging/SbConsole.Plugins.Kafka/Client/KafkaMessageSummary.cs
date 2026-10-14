namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Value (and Key, when present) is UTF-8 text when the raw bytes decode cleanly, otherwise
/// base64 with ValueIsBase64 = true -- see design spec §5. Key is null when the message has no key
/// (distinct from an empty-string key, which affects Kafka's default partitioner).
/// </summary>
public sealed record KafkaMessageSummary(
    int Partition, long Offset, DateTimeOffset Timestamp, string? Key, string Value, bool ValueIsBase64);
