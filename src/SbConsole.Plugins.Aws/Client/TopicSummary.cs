namespace SbConsole.Plugins.Aws.Client;

public sealed record TopicSummary(
    string Name, string TopicArn, bool IsFifo, int SubscriptionCount, int PendingConfirmationCount, bool IsKmsEncrypted,
    // True when this topic's own GetTopicAttributes call failed mid-listing (e.g. throttling) --
    // SubscriptionCount/PendingConfirmationCount/IsKmsEncrypted are meaningless zero/false defaults
    // in that case, not a real "no subscriptions" reading. ListTopicsAsync still includes the topic
    // (by name/ARN, both known from ListTopics itself) rather than failing the whole page; Topics.razor
    // renders a dash for this row's counts and suppresses the "no subscriptions" warning chip.
    bool AttributesUnavailable = false);
