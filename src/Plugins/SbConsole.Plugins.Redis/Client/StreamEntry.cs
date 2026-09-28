namespace SbConsole.Plugins.Redis.Client;

/// <summary>One field/value pair of a stream entry -- also the shape XADD's request takes (design spec §3a).</summary>
public sealed record StreamFieldValue(string Field, string Value);

/// <summary>
/// One stream entry (design spec §3a), read via XRANGE/XREVRANGE. Name collides with
/// StackExchange.Redis.StreamEntry -- same resolution story as <see cref="StreamConsumer"/>: this
/// type lives in SbConsole.Plugins.Redis.Client, so an unqualified `StreamEntry` inside that
/// namespace resolves here; the library type must be qualified as `StackExchange.Redis.StreamEntry`.
/// </summary>
public sealed record StreamEntry(string Id, IReadOnlyList<StreamFieldValue> Fields);
