namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// TotalLag is the sum of (high watermark - committed offset) across every topic-partition this
/// group has a committed offset for, clamped to >= 0 per partition. State is a Confluent.Kafka
/// ConsumerGroupState's ToString() (e.g. "Empty", "Stable", "Dead") -- kept as a plain string here
/// so this type has no Confluent.Kafka dependency at the IKafkaOperations seam, same reasoning
/// TopicSummary/KafkaMessageSummary already follow.
/// </summary>
public sealed record ConsumerGroupSummary(string GroupId, string State, int MemberCount, long TotalLag);
