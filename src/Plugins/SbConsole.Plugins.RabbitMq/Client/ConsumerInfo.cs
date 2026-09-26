namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>One entry of a queue's consumer_details. Prefetch 0 means unlimited.</summary>
public sealed record ConsumerInfo(string Tag, string ChannelName, int Prefetch, bool AckRequired, bool Exclusive);
