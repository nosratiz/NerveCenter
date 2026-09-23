namespace SbConsole.Plugins.ServiceBus.Client;

/// <summary>One queue or subscription with a non-zero dead-letter count, for one connection.
/// EntityType is "Queue" or "Subscription"; TopicName is null for a queue.</summary>
public sealed record DeadLetterEntry(string EntityType, string? TopicName, string EntityName, long Count);
