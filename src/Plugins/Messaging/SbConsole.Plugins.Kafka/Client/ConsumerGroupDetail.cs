namespace SbConsole.Plugins.Kafka.Client;

public sealed record ConsumerGroupDetail(
    string GroupId, string State,
    IReadOnlyList<ConsumerGroupPartitionLag> Partitions,
    IReadOnlyList<ConsumerGroupMember> Members);
