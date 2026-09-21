namespace SbConsole.Plugins.Aws.Client;

public sealed record ReceivedMessage(
    string MessageId, string ReceiptHandle, string Body, int ApproxReceiveCount,
    DateTimeOffset SentTimestamp, string SenderId, string Md5OfBody,
    IReadOnlyDictionary<string, string> MessageAttributes);
