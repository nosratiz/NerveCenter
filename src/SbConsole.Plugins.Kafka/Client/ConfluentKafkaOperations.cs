using Confluent.Kafka;
using Confluent.Kafka.Admin;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// The only real implementation of IKafkaOperations. Its own methods get light test coverage by
/// necessity -- they can't be meaningfully unit-tested without a real or emulated broker (design
/// spec §8) -- the substitutable interface is where the test leverage is.
/// </summary>
public sealed class ConfluentKafkaOperations : IKafkaOperations
{
    // Bounds every single blocking Confluent.Kafka call (GetMetadata, QueryWatermarkOffsets) so an
    // unreachable cluster fails fast with a clear message instead of hanging the page -- same role
    // AzureServiceBusOperations.AttemptTimeout plays for Service Bus.
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    internal static AdminClientConfig CreateAdminClientConfig(string config) => new(KafkaConfigParser.Parse(config));

    internal static ConsumerConfig CreateConsumerConfig(string config, string groupId) =>
        new(KafkaConfigParser.Parse(config)) { GroupId = groupId, EnableAutoCommit = false };

    internal static ProducerConfig CreateProducerConfig(string config) => new(KafkaConfigParser.Parse(config));

    // Confluent.Kafka's AdminClient/Consumer APIs (GetMetadata, QueryWatermarkOffsets, Assign,
    // Consume) are synchronous/blocking with no async overload -- Task.Run keeps every one of
    // these off the calling ASP.NET/Blazor circuit thread instead of blocking it for the length of
    // AttemptTimeout (or, for PeekMessagesAsync in Task 6, PeekWallClockCap).
    public Task<ConnectionTestResult> TestConnectionAsync(string config, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
                admin.GetMetadata(AttemptTimeout);
                return new ConnectionTestResult(true);
            }
            catch (KafkaException ex)
            {
                return new ConnectionTestResult(false, FriendlyKafkaError.From(ex));
            }
            catch (Exception ex)
            {
                return new ConnectionTestResult(false, FriendlyError.From(ex));
            }
        }, ct);

    public Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string config, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<TopicSummary>>(() =>
        {
            using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
            var metadata = admin.GetMetadata(AttemptTimeout);

            // One throwaway consumer group per call, purely to ask the cluster for watermark
            // offsets -- never subscribes, never commits. Same "fresh, never-reused group.id"
            // convention PeekMessagesAsync (Task 6) uses.
            using var consumer = new ConsumerBuilder<byte[], byte[]>(CreateConsumerConfig(config, Guid.NewGuid().ToString())).Build();

            var topics = new List<TopicSummary>();
            foreach (var topic in metadata.Topics)
            {
                var partitionCount = topic.Partitions.Count;
                // Replication factor is read from partition 0's replica count -- a topic with
                // non-uniform per-partition replication (possible after a manual reassignment
                // outside this tool) just shows that partition's count. See design spec §4.
                var replicationFactor = partitionCount > 0 ? topic.Partitions[0].Replicas.Length : 0;

                long approximateMessageCount = 0;
                foreach (var partition in topic.Partitions)
                {
                    var topicPartition = new TopicPartition(topic.Topic, new Partition(partition.PartitionId));
                    var watermarks = consumer.QueryWatermarkOffsets(topicPartition, AttemptTimeout);
                    approximateMessageCount += watermarks.High.Value - watermarks.Low.Value;
                }

                topics.Add(new TopicSummary(topic.Topic, partitionCount, replicationFactor, approximateMessageCount));
            }

            return topics;
        }, ct);

    public async Task CreateTopicAsync(string config, CreateTopicRequest request, CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
        await admin.CreateTopicsAsync(
        [
            new TopicSpecification
            {
                Name = request.Name,
                NumPartitions = request.PartitionCount,
                ReplicationFactor = (short)request.ReplicationFactor,
            }
        ]);
    }

    public async Task DeleteTopicAsync(string config, string topicName, CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
        await admin.DeleteTopicsAsync([topicName]);
    }

    // PeekMessagesAsync and ProduceMessageAsync are added in Task 6.
    public Task<IReadOnlyList<KafkaMessageSummary>> PeekMessagesAsync(
        string config, string topicName, int partition, PeekStart start, long? offset, int maxMessages, CancellationToken ct = default) =>
        throw new NotImplementedException("Added in Task 6.");

    public Task ProduceMessageAsync(string config, string topicName, string? key, string value, int? partition, CancellationToken ct = default) =>
        throw new NotImplementedException("Added in Task 6.");
}
