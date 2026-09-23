namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record TopicSummary(string Name, int SubscriptionCount, long SizeInBytes, long ScheduledMessageCount);
