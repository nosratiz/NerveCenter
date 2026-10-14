namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// A dynamic shovel, merged from its status (GET /api/shovels/{vhost}) and its definition
/// (GET /api/parameters/shovel/{vhost}). State is "running", "starting", "terminated" or
/// "unknown"; a definition with no status row yet is "starting". Reason is set for terminated
/// shovels.
/// </summary>
public sealed record ShovelInfo(
    string Name,
    string State,
    string? Reason,
    string? SourceQueue,
    string? SourceExchange,
    string SourceUri,
    string? DestinationQueue,
    string? DestinationExchange,
    string? DestinationRoutingKey,
    string DestinationUri,
    string AckMode,
    DateTimeOffset? Timestamp);
