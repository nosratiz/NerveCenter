namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// GET /api/queues/{vhost} (or /api/queues for every vhost -- Vhost says which). Ready and
/// Unacked are deliberately separate and never summed anywhere in the UI (design spec §1).
/// DeadLetterExchange/RoutingKey, MessageTtlMs, MaxLength and Overflow resolve the queue argument
/// first and fall back to the effective policy definition -- an argument beats a policy in
/// RabbitMQ for these keys. Rates are null until the broker has sampled them.
/// </summary>
public sealed record QueueSummary(
    string Vhost,
    string Name,
    string Type,
    bool Durable,
    bool AutoDelete,
    bool Exclusive,
    long Ready,
    long Unacked,
    int Consumers,
    double? PublishRate,
    double? AckRate,
    double? RedeliverRate,
    string? DeadLetterExchange,
    string? DeadLetterRoutingKey,
    long? MessageTtlMs,
    long? MaxLength,
    string? Overflow,
    bool Lazy,
    string? Policy,
    DateTimeOffset? IdleSince,
    long MemoryBytes,
    int? ReplicaCount,
    DateTimeOffset? HeadMessageTimestamp,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlyDictionary<string, object?> EffectivePolicyDefinition);
