namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// ApproximateMessageCount is the sum of (high - low watermark) across every partition -- what the
/// topic's retention policy is currently holding, NOT an "unprocessed backlog" the way Service
/// Bus's ActiveMessageCount is (Kafka never removes a message on consumption). See design spec §4.
/// </summary>
public sealed record TopicSummary(string Name, int PartitionCount, int ReplicationFactor, long ApproximateMessageCount);
