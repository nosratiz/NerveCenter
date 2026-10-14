namespace SbConsole.Plugins.Redis.Client;

/// <summary>One message delivered to a live PUBSUB/PSUBSCRIBE tail (design spec §14). ReceivedAt is
/// this client's own receipt time -- Redis PUBLISH carries no server-side timestamp.</summary>
public sealed record PubSubMessage(string Channel, string Payload, DateTimeOffset ReceivedAt);
