namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// GET /api/exchanges/{vhost}. Name "" is the AMQP default exchange (the management API calls it
/// "amq.default" in URLs but "" in listings). Rates are null until the broker has sampled them.
/// </summary>
public sealed record ExchangeSummary(
    string Name,
    string Type,
    bool Durable,
    bool AutoDelete,
    bool Internal,
    string? AlternateExchange,
    double? PublishInRate,
    double? PublishOutRate,
    IReadOnlyDictionary<string, object?> Arguments)
{
    public bool IsDefault => Name.Length == 0;

    /// <summary>The default exchange and the broker-declared amq.* exchanges; neither can be deleted.</summary>
    public bool IsBuiltIn => IsDefault || Name.StartsWith("amq.", StringComparison.Ordinal);
}
