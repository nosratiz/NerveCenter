namespace SbConsole.Plugins.Aws.Client;

public sealed record TopicSummary(
    string Name, string TopicArn, bool IsFifo, int SubscriptionCount, int PendingConfirmationCount, bool IsKmsEncrypted);
