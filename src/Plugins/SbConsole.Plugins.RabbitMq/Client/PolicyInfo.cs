namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>GET /api/policies/{vhost}. ApplyTo is "queues", "exchanges", "all" (or "classic_queues"/"quorum_queues"/"streams" on 3.12+).</summary>
public sealed record PolicyInfo(
    string Name,
    string Pattern,
    string ApplyTo,
    int Priority,
    IReadOnlyDictionary<string, object?> Definition);
