namespace SbConsole.Plugins.Aws.Client;

public sealed record QueueSummary(
    string Name, string QueueUrl, string QueueArn, bool IsFifo,
    long ApproxVisible, long ApproxInFlight, long ApproxDelayed,
    // HasDeadLetterTarget: this queue has its OWN RedrivePolicy, i.e. it is a *source* queue that
    // sends failed messages elsewhere -- NOT a signal that this queue is a dead-letter queue. Kept
    // for a possible future queue-detail "redrive-out" panel; the DLQ chip/Redrive button in
    // Queues.razor must use DeadLetterSourceCount below instead.
    bool HasDeadLetterTarget, bool IsKmsEncrypted, DateTimeOffset CreatedAt,
    // Number of OTHER queues whose RedrivePolicy targets this queue's ARN -- i.e. how many source
    // queues redrive into this one. This is the actual "is this a DLQ, and can Redrive be started
    // from it" signal, since AWS's StartMessageMoveTask requires SourceArn to be a queue that is
    // itself a redrive target.
    int DeadLetterSourceCount = 0,
    // True when this queue's own GetQueueAttributes call failed mid-listing (e.g. throttling) --
    // every other field is a meaningless default in that case. ListQueuesAsync still includes the
    // queue (by name/URL, both known from ListQueues itself) rather than failing the whole page;
    // Queues.razor renders a dash in the count columns for a row with this set.
    bool AttributesUnavailable = false)
{
    // Represents a queue whose GetQueueAttributes call failed -- name/URL are the only trustworthy
    // fields (they come from ListQueues, not the failed call). QueueArn is unknown, so this queue
    // can never appear as a redrive target/source for DLQ-counting purposes.
    public static QueueSummary Unavailable(string name, string queueUrl) => new(
        Name: name, QueueUrl: queueUrl, QueueArn: "", IsFifo: false,
        ApproxVisible: 0, ApproxInFlight: 0, ApproxDelayed: 0,
        HasDeadLetterTarget: false, IsKmsEncrypted: false, CreatedAt: DateTimeOffset.MinValue,
        DeadLetterSourceCount: 0, AttributesUnavailable: true);
}
