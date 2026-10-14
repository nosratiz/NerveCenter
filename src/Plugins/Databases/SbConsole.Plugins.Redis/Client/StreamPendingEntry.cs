namespace SbConsole.Plugins.Redis.Client;

/// <summary>One row in a stream group's pending-entries list (design spec §3a/§2), from XPENDING's extended form.</summary>
public sealed record StreamPendingEntry(string Id, string Consumer, TimeSpan Idle, int DeliveryCount);
