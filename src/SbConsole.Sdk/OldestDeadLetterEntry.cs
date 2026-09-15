namespace SbConsole.Sdk;

/// <summary>
/// The single oldest dead-lettered message a plugin found across all of one connection's
/// resources, for the wallboard's "Oldest message" tile. Real data from a live peek -- there is
/// no honest way to approximate one message's enqueue time from aggregate counts.
/// </summary>
public sealed record OldestDeadLetterEntry(string ResourceName, DateTimeOffset EnqueuedTime, long DeadLetterCount);
