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
}
