namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record PeekedMessage(
    long SequenceNumber,
    string Body,
    string? ContentType,
    DateTimeOffset EnqueuedTime,
    int DeliveryCount,
    IReadOnlyDictionary<string, string> Properties,
    string? DeadLetterReason = null,
    string? DeadLetterErrorDescription = null);
