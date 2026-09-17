using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// The seam between the plugin's handlers and the real Confluent.Kafka client. Every method takes
/// the connection config string as a parameter -- no instance is pre-configured for one connection
/// -- since a single registered instance tests and operates against whatever connection the caller
/// names. Mirrors SbConsole.Plugins.ServiceBus.Client.IServiceBusOperations.
/// </summary>
public interface IKafkaOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string config, CancellationToken ct = default);

    Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string config, CancellationToken ct = default);

    /// <summary>
    /// Lightweight counterpart to ListTopicsAsync for callers that only need topic/partition
    /// counts (e.g. the Dashboard and Wallboard): reads GetMetadata alone, with no per-partition
    /// QueryWatermarkOffsets round trip.
    /// </summary>
    Task<(int TopicCount, int PartitionCount)> GetTopicCountsAsync(string config, CancellationToken ct = default);

    Task CreateTopicAsync(string config, CreateTopicRequest request, CancellationToken ct = default);

    /// <summary>Destructive.</summary>
    Task DeleteTopicAsync(string config, string topicName, CancellationToken ct = default);

    /// <summary>
    /// Non-destructive: a fresh, never-reused consumer group per call, never committing an offset.
    /// offset is read only when start is PeekStart.Offset.
    /// </summary>
    Task<PeekResult> PeekMessagesAsync(
        string config, string topicName, int partition, PeekStart start, long? offset, int maxMessages, CancellationToken ct = default);

    /// <summary>partition null lets Kafka's default partitioner choose (by key hash, or round-robin when key is null).</summary>
    Task ProduceMessageAsync(string config, string topicName, string? key, string value, int? partition, CancellationToken ct = default);
}
