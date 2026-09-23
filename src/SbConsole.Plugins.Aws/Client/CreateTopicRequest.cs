namespace SbConsole.Plugins.Aws.Client;

public sealed record CreateTopicRequest(string Name, bool IsFifo, string? KmsKeyId, bool? ContentBasedDeduplication);
