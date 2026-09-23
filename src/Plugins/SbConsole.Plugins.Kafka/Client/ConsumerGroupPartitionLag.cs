namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// ConsumerClientId/ConsumerHost are null when the partition has a committed offset but no member
/// is currently assigned to it (an idle group, or a partition simply unassigned right now) -- see
/// design spec §3.
/// </summary>
public sealed record ConsumerGroupPartitionLag(
    string TopicName, int Partition, long CommittedOffset, long HighWatermark, long Lag,
    string? ConsumerClientId, string? ConsumerHost);
