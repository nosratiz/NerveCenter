namespace SbConsole.Plugins.Aws.Client;

public sealed record QueueSummary(
    string Name, string QueueUrl, string QueueArn, bool IsFifo,
    long ApproxVisible, long ApproxInFlight, long ApproxDelayed,
    bool HasDeadLetterTarget, bool IsKmsEncrypted, DateTimeOffset CreatedAt);
