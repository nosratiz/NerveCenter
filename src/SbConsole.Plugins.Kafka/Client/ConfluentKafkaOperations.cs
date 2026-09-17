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

    // Without these, message.timeout.ms/socket.timeout.ms stay at librdkafka's defaults (300000ms /
    // 60000ms) and ProduceMessageAsync can hang for minutes against an unreachable cluster --
    // verified empirically against the installed Confluent.Kafka 2.15.1 (ProduceAsync to an
    // unreachable broker had not completed after 5+ seconds). Bounding both to AttemptTimeout gives
    // produce the same fail-fast behavior TestConnectionAsync/ListTopicsAsync already have.
    internal static ProducerConfig CreateProducerConfig(string config) =>
        new(KafkaConfigParser.Parse(config))
        {
            MessageTimeoutMs = (int)AttemptTimeout.TotalMilliseconds,
            SocketTimeoutMs = (int)AttemptTimeout.TotalMilliseconds,
        };

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

    // Same Task.Run + AdminClientBuilder + GetMetadata shape ListTopicsAsync/TestConnectionAsync
    // already use, but stops after summing partition counts -- no ConsumerBuilder, no
    // QueryWatermarkOffsets at all, since the Dashboard/Wallboard callers only need the two counts.
    public Task<(int TopicCount, int PartitionCount)> GetTopicCountsAsync(string config, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
            var metadata = admin.GetMetadata(AttemptTimeout);
            return (metadata.Topics.Count, metadata.Topics.Sum(t => t.Partitions.Count));
        }, ct);

    internal static CreateTopicsOptions BuildCreateTopicsOptions() => new() { RequestTimeout = AttemptTimeout };

    internal static DeleteTopicsOptions BuildDeleteTopicsOptions() => new() { RequestTimeout = AttemptTimeout };

    // ct is intentionally unused by both methods below: verified against the installed
    // Confluent.Kafka 2.15.1 (via decompilation) that IAdminClient.CreateTopicsAsync/
    // DeleteTopicsAsync have exactly one overload each -- (topics, options) -- with no
    // CancellationToken parameter or overload anywhere on IAdminClient. RequestTimeout on the
    // options object above is this API's only way to bound the call, replacing librdkafka's
    // default admin timeout with AttemptTimeout, same role AttemptTimeout plays everywhere else
    // in this class.
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
        ], BuildCreateTopicsOptions());
    }

    public async Task DeleteTopicAsync(string config, string topicName, CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
        await admin.DeleteTopicsAsync([topicName], BuildDeleteTopicsOptions());
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
                if (IsEndOfPartition(result))
                {
                    break;
                }

                if (result is null)
                {
                    continue; // poll timeout, not end of data -- PeekWallClockCap bounds the loop overall
                }

                messages.Add(ToSummary(result));
            }

            return new PeekResult(messages, watermarks.Low.Value, watermarks.High.Value);
        }, ct);

    // Consume(TimeSpan) returns null on a plain poll timeout (nothing ready in that 2s slice) --
    // a completely different condition from IsPartitionEOF (no more messages in the partition right
    // now). Conflating the two used to make Peek silently return an empty message list on a real
    // remote cluster whenever the first Consume()'s broker connect (TCP+TLS+SASL+metadata+fetch)
    // took longer than the poll slice. Extracted as its own predicate, separate from the null check
    // above, so the null-vs-EOF distinction is unit-testable without a real broker --
    // ConsumeResult<TKey,TValue> is a plain settable POCO, unlike IConsumer itself (design spec §8).
    internal static bool IsEndOfPartition(ConsumeResult<byte[], byte[]>? result) => result is { IsPartitionEOF: true };

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

    // Extracted as a pure static function so the "clamp to >= 0" rule (design spec §3 -- a
    // committed offset can be momentarily ahead of a just-moved watermark) is unit-testable
    // without a real broker, same reasoning as IsEndOfPartition/Decode above.
    internal static long ComputeLag(long committedOffset, long highWatermark) => Math.Max(0, highWatermark - committedOffset);

    // Shared by ListConsumerGroupsAsync (below) and GetConsumerGroupDetailAsync (Task 3). Passing
    // null for topicPartitions is librdkafka's documented way to mean "every topic-partition this
    // group has a committed offset for" -- see this plan's Global Constraints; not asserted by any
    // .NET doc comment, so it isn't (and can't be) covered by a unit test.
    internal static async Task<IReadOnlyList<TopicPartitionOffsetError>> GetCommittedOffsetsAsync(IAdminClient admin, string groupId)
    {
        var results = await admin.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(groupId, null)],
            new ListConsumerGroupOffsetsOptions { RequestTimeout = AttemptTimeout });
        return results[0].Partitions;
    }

    public async Task<IReadOnlyList<ConsumerGroupSummary>> ListConsumerGroupsAsync(string config, CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();

        var listResult = await admin.ListConsumerGroupsAsync(new ListConsumerGroupsOptions { RequestTimeout = AttemptTimeout });
        if (listResult.Valid.Count == 0)
        {
            return [];
        }

        var groupIds = listResult.Valid.Select(g => g.GroupId).ToList();
        var describeResult = await admin.DescribeConsumerGroupsAsync(groupIds, new DescribeConsumerGroupsOptions { RequestTimeout = AttemptTimeout });
        var memberCountByGroup = describeResult.ConsumerGroupDescriptions.ToDictionary(d => d.GroupId, d => d.Members.Count);

        // One throwaway consumer group, reused across every group's watermark lookups below -- same
        // "fresh, never-reused group.id, purely to ask the cluster for watermark offsets" convention
        // ListTopicsAsync already uses.
        using var consumer = new ConsumerBuilder<byte[], byte[]>(CreateConsumerConfig(config, Guid.NewGuid().ToString())).Build();

        var summaries = new List<ConsumerGroupSummary>();
        foreach (var listing in listResult.Valid)
        {
            var offsets = await GetCommittedOffsetsAsync(admin, listing.GroupId);
            var totalLag = await Task.Run(() =>
            {
                long total = 0;
                foreach (var partition in offsets)
                {
                    if (partition.Error.IsError)
                    {
                        continue;
                    }

                    var watermarks = consumer.QueryWatermarkOffsets(partition.TopicPartition, AttemptTimeout);
                    total += ComputeLag(partition.Offset.Value, watermarks.High.Value);
                }

                return total;
            }, ct);

            summaries.Add(new ConsumerGroupSummary(listing.GroupId, listing.State.ToString(), memberCountByGroup.GetValueOrDefault(listing.GroupId, 0), totalLag));
        }

        return summaries;
    }
}
