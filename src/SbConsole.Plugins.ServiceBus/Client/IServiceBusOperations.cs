using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Client;

/// <summary>
/// The seam between the plugin's handlers and the real Azure SDK. Every method takes the
/// connection string as a parameter — no instance is pre-configured for one connection — since
/// a single registered instance tests and operates against whatever connection the caller names.
/// </summary>
public interface IServiceBusOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default);

    Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string connectionString, CancellationToken ct = default);

    Task CreateQueueAsync(string connectionString, CreateQueueRequest request, CancellationToken ct = default);

    /// <summary>Destructive.</summary>
    Task DeleteQueueAsync(string connectionString, string queueName, CancellationToken ct = default);

    /// <summary>Non-destructive. Set fromDeadLetter to browse the queue's dead-letter sub-queue instead.</summary>
    Task<IReadOnlyList<PeekedMessage>> PeekMessagesAsync(
        string connectionString, string queueName, bool fromDeadLetter, int maxMessages,
        long? fromSequenceNumber = null, CancellationToken ct = default);

    Task SendMessageAsync(string connectionString, string queueName, SendMessageRequest request, CancellationToken ct = default);

    /// <summary>Moves the named dead-lettered messages (by sequence number) back onto the main queue. Returns how many were actually found and resubmitted.</summary>
    Task<int> ResubmitDeadLetterMessagesAsync(string connectionString, string queueName, IReadOnlyList<long> sequenceNumbers, CancellationToken ct = default);

    /// <summary>Destructive. Drains and discards every message currently in the queue's dead-letter sub-queue. Returns how many were purged.</summary>
    Task<int> PurgeDeadLetterMessagesAsync(string connectionString, string queueName, CancellationToken ct = default);

    Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string connectionString, CancellationToken ct = default);

    Task CreateTopicAsync(string connectionString, CreateTopicRequest request, CancellationToken ct = default);

    /// <summary>Destructive. Cascades: deletes every subscription on the topic too.</summary>
    Task DeleteTopicAsync(string connectionString, string topicName, CancellationToken ct = default);

    Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string connectionString, string topicName, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="ListSubscriptionsAsync"/> but with a real <c>Status</c> merged in from a
    /// second admin-client call (config properties, not runtime properties) -- an extra Azure
    /// round trip only the caller that actually needs the config status (currently the Dashboard's
    /// disabled-subscription check) should pay for. Every other caller should keep using
    /// <see cref="ListSubscriptionsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsWithStatusAsync(string connectionString, string topicName, CancellationToken ct = default);

    Task CreateSubscriptionAsync(string connectionString, string topicName, CreateSubscriptionRequest request, CancellationToken ct = default);

    /// <summary>Destructive.</summary>
    Task DeleteSubscriptionAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default);

    /// <summary>Every queue and subscription with a non-zero dead-letter count for this connection.
    /// One seam shared by the Dead-letter nav badge and the Dead-letter overview page.</summary>
    Task<IReadOnlyList<DeadLetterEntry>> ListDeadLetterEntriesAsync(string connectionString, CancellationToken ct = default);

    /// <summary>Non-destructive. Set fromDeadLetter to browse the subscription's dead-letter sub-queue instead.</summary>
    Task<IReadOnlyList<PeekedMessage>> PeekSubscriptionMessagesAsync(
        string connectionString, string topicName, string subscriptionName, bool fromDeadLetter, int maxMessages,
        long? fromSequenceNumber = null, CancellationToken ct = default);

    /// <summary>
    /// Moves the named dead-lettered messages (by sequence number) back into normal delivery by
    /// republishing them onto the topic -- Service Bus has no way to inject a message directly into
    /// one subscription's queue, so a resubmitted message is re-evaluated against every
    /// subscription's filters, not routed back only to this one. Returns how many were actually
    /// found and resubmitted.
    /// </summary>
    Task<int> ResubmitSubscriptionDeadLetterMessagesAsync(string connectionString, string topicName, string subscriptionName, IReadOnlyList<long> sequenceNumbers, CancellationToken ct = default);

    /// <summary>Destructive. Drains and discards every message currently in the subscription's dead-letter sub-queue. Returns how many were purged.</summary>
    Task<int> PurgeSubscriptionDeadLetterMessagesAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default);
}
