namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>One entry of a message's x-death header: where it died, why, and how many times.</summary>
public sealed record DeathRecord(
    string Queue,
    string Exchange,
    string Reason,
    IReadOnlyList<string> RoutingKeys,
    long Count,
    DateTimeOffset? Time);
