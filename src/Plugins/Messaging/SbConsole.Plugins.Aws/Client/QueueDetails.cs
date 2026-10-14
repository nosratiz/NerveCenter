namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// A queue's OWN RedrivePolicy -- where this queue sends messages that exceed MaxReceiveCount.
/// MaxReceiveCount is null when AWS omits it or shapes it unexpectedly.
/// </summary>
public sealed record QueueRedrivePolicy(string DeadLetterTargetArn, int? MaxReceiveCount)
{
    public string DeadLetterTargetName => DeadLetterTargetArn[(DeadLetterTargetArn.LastIndexOf(':') + 1)..];
}

/// <summary>
/// Everything the queue detail page shows for one queue. Named QueueDetails (not QueueDetail) so it
/// never collides with the Pages.QueueDetail component inside that page's own code.
/// </summary>
public sealed record QueueDetails(
    string Name, string QueueUrl, string QueueArn, bool IsFifo, bool IsKmsEncrypted,
    long ApproxVisible, long ApproxInFlight, long ApproxDelayed,
    // Null when the attribute is missing -- never a 1970 epoch masquerading as a real date.
    DateTimeOffset? CreatedAt, DateTimeOffset? LastModifiedAt,
    // Every raw attribute GetQueueAttributes(All) returned, unfiltered.
    IReadOnlyDictionary<string, string> Attributes,
    // Null when ListQueueTags failed (it's a separate IAM permission, sqs:ListQueueTags) -- the page
    // renders "unavailable" rather than a misleading "no tags".
    IReadOnlyDictionary<string, string>? Tags,
    QueueRedrivePolicy? RedrivePolicy,
    // URLs of the queues whose RedrivePolicy targets this one (SQS ListDeadLetterSourceQueues). Null
    // when that call failed -- same "unavailable, not empty" rule as Tags.
    IReadOnlyList<string>? DeadLetterSourceQueueUrls)
{
    /// <summary>Other queues redrive into this one -- same signal as QueueSummary.DeadLetterSourceCount &gt; 0.</summary>
    public bool IsDeadLetterQueue => DeadLetterSourceQueueUrls is { Count: > 0 };
}
