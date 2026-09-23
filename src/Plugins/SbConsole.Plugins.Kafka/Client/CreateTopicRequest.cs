namespace SbConsole.Plugins.Kafka.Client;

public sealed record CreateTopicRequest(string Name, int PartitionCount, int ReplicationFactor);
