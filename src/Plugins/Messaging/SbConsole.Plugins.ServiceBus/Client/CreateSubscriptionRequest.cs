namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record CreateSubscriptionRequest(
    string Name,
    int MaxDeliveryCount = 10,
    TimeSpan? LockDuration = null,
    TimeSpan? DefaultMessageTimeToLive = null);
