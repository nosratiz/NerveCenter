namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// LowWatermark/HighWatermark are the partition's current bounds at fetch time (not affected by
/// which messages were actually returned) -- shown in the UI next to the partition picker so
/// "Latest" and "Earliest" mean something concrete to the person choosing them.
/// </summary>
public sealed record PeekResult(IReadOnlyList<KafkaMessageSummary> Messages, long LowWatermark, long HighWatermark);
