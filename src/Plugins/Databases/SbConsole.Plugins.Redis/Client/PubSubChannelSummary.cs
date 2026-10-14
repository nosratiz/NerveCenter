namespace SbConsole.Plugins.Redis.Client;

/// <summary>One row of PUBSUB CHANNELS [pattern] + PUBSUB NUMSUB (design spec §14).</summary>
public sealed record PubSubChannelSummary(string Channel, long SubscriberCount);
