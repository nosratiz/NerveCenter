namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record SubscriptionSummary(
    string Name,
    long ActiveMessageCount,
    long DeadLetterMessageCount,
    long TotalMessageCount);
