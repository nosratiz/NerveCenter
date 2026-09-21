namespace SbConsole.Plugins.Aws.Client;

public sealed record SendMessageRequest(
    string Body, IReadOnlyDictionary<string, string>? MessageAttributes, int? DelaySeconds,
    string? MessageGroupId, string? MessageDeduplicationId);
