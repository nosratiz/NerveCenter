namespace SbConsole.Plugins.Kafka.Client;

public sealed record TopicPartitionRef(string TopicName, int Partition);
