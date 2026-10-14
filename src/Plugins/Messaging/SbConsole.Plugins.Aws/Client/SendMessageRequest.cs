namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// MessageAttributes are plain strings, sent with DataType "String" (the Send dialog's key/value
/// rows). TypedMessageAttributes carry a type and binary value each and are sent unchanged (Move to
/// source resending a received message); when both are given, a typed entry wins for its key.
/// </summary>
public sealed record SendMessageRequest(
    string Body, IReadOnlyDictionary<string, string>? MessageAttributes, int? DelaySeconds,
    string? MessageGroupId, string? MessageDeduplicationId,
    IReadOnlyDictionary<string, SqsMessageAttribute>? TypedMessageAttributes = null);
