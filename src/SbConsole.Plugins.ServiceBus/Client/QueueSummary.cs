namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record QueueSummary(
    string Name,
    long ActiveMessageCount,
    long DeadLetterMessageCount,
    long ScheduledMessageCount,
    long SizeInBytes);
