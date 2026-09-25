namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// What Test connection's management-API probe learned (design spec §2): GET /api/overview for the
/// version, GET /api/whoami for the user's tags, and the exchange/queue counts in the connection's
/// configured vhost. Latency is measured by the caller, not stored here.
/// </summary>
public sealed record ManagementProbe(
    string RabbitVersion,
    string ClusterName,
    int ExchangeCount,
    int QueueCount,
    IReadOnlyList<string> UserTags);
