namespace SbConsole.Plugins.Aws.Client;

public sealed record ReceivedMessage(
    string MessageId, string ReceiptHandle, string Body, int ApproxReceiveCount,
    DateTimeOffset SentTimestamp, string SenderId, string Md5OfBody,
    IReadOnlyDictionary<string, string> MessageAttributes,
    // The MessageGroupId system attribute -- present on FIFO-queue messages, null otherwise. Needed to
    // re-send a dead-lettered FIFO message to its source queue ("Move to source" on Receive).
    string? MessageGroupId = null);
