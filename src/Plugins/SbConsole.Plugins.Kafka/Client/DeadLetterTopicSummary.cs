namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// ApproximateMessageCount carries the same caveat TopicSummary's does (design spec 2026-09-16
/// §4): "currently retained by the topic's retention policy," not "unprocessed backlog" -- Kafka
/// has no such concept. OldestMessageTimestamp is null when every partition is empty (nothing
/// retained right now); otherwise the earliest message's timestamp across all of this topic's
/// partitions.
/// </summary>
public sealed record DeadLetterTopicSummary(
    string DlqTopicName, string OriginalTopicName, int PartitionCount,
    long ApproximateMessageCount, DateTimeOffset? OldestMessageTimestamp);
