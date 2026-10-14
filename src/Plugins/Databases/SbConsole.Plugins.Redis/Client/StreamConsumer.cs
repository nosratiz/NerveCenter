namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// One consumer within one stream's group (design spec §3a), from XINFO CONSUMERS -- which is
/// where the Idle field comes from; StackExchange.Redis's own lighter-weight `StreamConsumer`
/// type (Name/PendingMessageCount only, returned inside StreamPendingInfo.Consumers from XPENDING's
/// summary form) has no idle time, so this DTO is sourced from StreamConsumerInfoAsync instead.
/// Name collides with StackExchange.Redis.StreamConsumer/StreamConsumerInfo -- both are distinct
/// library types in the `StackExchange.Redis` namespace, so C#'s "declarations in the current
/// namespace win over using-imported ones" rule resolves an unqualified `StreamConsumer` in this
/// project's own SbConsole.Plugins.Redis.Client namespace to this type with no ambiguity; code that
/// needs the library type instead must qualify it as `StackExchange.Redis.StreamConsumer`.
/// </summary>
public sealed record StreamConsumer(string Name, long PendingCount, TimeSpan Idle);
