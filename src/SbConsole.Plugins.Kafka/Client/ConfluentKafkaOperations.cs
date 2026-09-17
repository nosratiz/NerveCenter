using System.Text;
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

    // Bounds the whole peek call, not just one Consume() -- a topic/partition with fewer messages
    // than maxMessages must return early with what it got, not hang until this expires. Mirrors
    // AzureServiceBusOperations.BulkOperationTimeout's role, sized down since peek is interactive,
    // not a bulk drain.
    internal static readonly TimeSpan PeekWallClockCap = TimeSpan.FromSeconds(30);

    public Task<PeekResult> PeekMessagesAsync(
        string config, string topicName, int partition, PeekStart start, long? offset, int maxMessages, CancellationToken ct = default) =>
        Task.Run<PeekResult>(() =>
        {
            // A fresh, never-reused group.id every call -- peeking never commits an offset and
            // never shares a consumer group with anything else. See design spec §5.
            var consumerConfig = CreateConsumerConfig(config, Guid.NewGuid().ToString());
            consumerConfig.EnablePartitionEof = true;
            using var consumer = new ConsumerBuilder<byte[], byte[]>(consumerConfig).Build();

            var topicPartition = new TopicPartition(topicName, new Partition(partition));
            var watermarks = consumer.QueryWatermarkOffsets(topicPartition, AttemptTimeout);

            var startOffset = start switch
            {
                PeekStart.Earliest => Offset.Beginning,
                PeekStart.Offset => new Offset(offset ?? 0),
                _ => new Offset(Math.Max(watermarks.High.Value - maxMessages, watermarks.Low.Value)),
            };

            consumer.Assign(new TopicPartitionOffset(topicPartition, startOffset));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PeekWallClockCap);

            var messages = new List<KafkaMessageSummary>();
            while (messages.Count < maxMessages && !timeoutCts.IsCancellationRequested)
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(2));
                if (result is null || result.IsPartitionEOF)
                {
                    break;
                }

                messages.Add(ToSummary(result));
            }

            return new PeekResult(messages, watermarks.Low.Value, watermarks.High.Value);
        }, ct);

    private static KafkaMessageSummary ToSummary(ConsumeResult<byte[], byte[]> result)
    {
        var (value, valueIsBase64) = Decode(result.Message.Value);
        var key = result.Message.Key is { Length: > 0 } keyBytes ? Decode(keyBytes).Text : null;
        return new KafkaMessageSummary(result.Partition.Value, result.Offset.Value, result.Message.Timestamp.UtcDateTime, key, value, valueIsBase64);
    }

    // Internal (not private) so ConfluentKafkaOperationsTests can assert it directly --
    // InternalsVisibleTo already covers the test project (Task 1's csproj).
    internal static (string Text, bool IsBase64) Decode(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return (text, false);
        }
        catch (DecoderFallbackException)
        {
            return (Convert.ToBase64String(bytes), true);
        }
    }

    public async Task ProduceMessageAsync(string config, string topicName, string? key, string value, int? partition, CancellationToken ct = default)
    {
        using var producer = new ProducerBuilder<string?, string>(CreateProducerConfig(config)).Build();
        var message = new Message<string?, string> { Key = key, Value = value };
        var topicPartition = new TopicPartition(topicName, partition is { } p ? new Partition(p) : Partition.Any);
        await producer.ProduceAsync(topicPartition, message, ct);
    }
}
