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
    int DeadLetterSourceCount = 0);
