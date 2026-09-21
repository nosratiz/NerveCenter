namespace SbConsole.Plugins.Aws.Client;

public sealed record CreateQueueRequest(
    string Name, bool IsFifo,
    int VisibilityTimeoutSeconds, int RetentionPeriodSeconds, int DelaySeconds, int MaxMessageSizeBytes, int ReceiveWaitTimeSeconds,
    string? DeadLetterTargetArn, int? MaxReceiveCount,
    string? KmsKeyId,
    bool? ContentBasedDeduplication, bool? HighThroughputFifo);
