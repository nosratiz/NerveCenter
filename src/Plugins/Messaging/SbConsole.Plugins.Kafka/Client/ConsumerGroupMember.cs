namespace SbConsole.Plugins.Kafka.Client;

public sealed record ConsumerGroupMember(string ClientId, string? Host, IReadOnlyList<TopicPartitionRef> AssignedPartitions);
