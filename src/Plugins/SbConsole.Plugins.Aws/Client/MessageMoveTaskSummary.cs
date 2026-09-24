namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// One entry from SQS ListMessageMoveTasks (a redrive out of a dead-letter queue). TaskHandle is
/// only returned by AWS for RUNNING tasks, and is what CancelMessageMoveTask needs. MessagesToMove
/// is null when AWS didn't report it (AWSSDK.SQS 3.7 surfaces an absent value as 0).
/// </summary>
public sealed record MessageMoveTaskSummary(
    string? TaskHandle, string Status, string SourceArn, string? DestinationArn,
    long MessagesMoved, long? MessagesToMove, string? FailureReason, DateTimeOffset? StartedAt)
{
    public const string RunningStatus = "RUNNING";

    public bool IsRunning => Status == RunningStatus;

    public double? ProgressPercent => MessagesToMove is > 0 and var total
        ? Math.Clamp(MessagesMoved * 100.0 / total, 0, 100)
        : null;
}
