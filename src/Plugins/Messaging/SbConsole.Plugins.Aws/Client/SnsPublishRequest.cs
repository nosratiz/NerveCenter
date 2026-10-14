namespace SbConsole.Plugins.Aws.Client;

public sealed record SnsPublishRequest(
    string? Subject, string Message, IReadOnlyDictionary<string, string>? MessageAttributes,
    string? MessageGroupId, string? MessageDeduplicationId);
