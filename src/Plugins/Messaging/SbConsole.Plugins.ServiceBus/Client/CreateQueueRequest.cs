namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record CreateQueueRequest(
    string Name,
    int MaxDeliveryCount = 10,
    TimeSpan? LockDuration = null,
    TimeSpan? DefaultMessageTimeToLive = null);
