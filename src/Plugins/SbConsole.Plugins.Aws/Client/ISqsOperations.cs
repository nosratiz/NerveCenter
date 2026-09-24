using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The seam between the plugin's handlers and the real AWSSDK.SQS/AWSSDK.SecurityToken clients.
/// Every method takes the connection secret as a parameter -- no instance is pre-configured for
/// one connection -- since a single registered instance tests and operates against whatever
/// connection the caller names. Mirrors SbConsole.Plugins.Kafka.Client.IKafkaOperations.
/// </summary>
public interface ISqsOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? namePrefix, CancellationToken ct = default);
    /// <summary>
    /// GetQueueAttributes(All) + ListQueueTags + ListDeadLetterSourceQueues for one queue. Only the
    /// attributes call is required -- a failed tags/sources call degrades that field to null.
    /// </summary>
    Task<QueueDetails> GetQueueDetailAsync(string secret, string queueUrl, CancellationToken ct = default);
    Task<string> CreateQueueAsync(string secret, CreateQueueRequest request, CancellationToken ct = default);
    /// <summary>Destructive.</summary>
    Task DeleteQueueAsync(string secret, string queueUrl, CancellationToken ct = default);
    /// <summary>Destructive. Asynchronous and eventually consistent on the AWS side (design spec §5).</summary>
    Task PurgeQueueAsync(string secret, string queueUrl, CancellationToken ct = default);

    Task<IReadOnlyList<ReceivedMessage>> ReceiveMessagesAsync(
        string secret, string queueUrl, int maxMessages, int? visibilityTimeoutSeconds, int waitTimeSeconds, CancellationToken ct = default);
    /// <summary>Requires the receipt handle from ReceiveMessagesAsync, not the message ID.</summary>
    Task DeleteMessageAsync(string secret, string queueUrl, string receiptHandle, CancellationToken ct = default);
    /// <summary>"Release now" -- sets visibility timeout to 0 so the message is immediately visible again.</summary>
    Task ChangeMessageVisibilityAsync(string secret, string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken ct = default);
    Task SendMessageAsync(string secret, string queueUrl, SendMessageRequest request, CancellationToken ct = default);

    /// <summary>Native AWS move task (StartMessageMoveTask) -- destination defaults to the queue the source dead-lettered from.</summary>
    Task<string> StartRedriveTaskAsync(string secret, string sourceQueueArn, string destinationQueueArn, int? maxMessagesPerSecond, CancellationToken ct = default);
    /// <summary>ListMessageMoveTasks for a DLQ -- the most recent move tasks (AWS returns at most 10), newest first.</summary>
    Task<IReadOnlyList<MessageMoveTaskSummary>> ListMessageMoveTasksAsync(string secret, string sourceQueueArn, CancellationToken ct = default);
    /// <summary>CancelMessageMoveTask -- only valid while the task is RUNNING. Messages already moved stay moved.</summary>
    Task CancelMessageMoveTaskAsync(string secret, string taskHandle, CancellationToken ct = default);
}
