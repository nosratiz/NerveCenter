namespace SbConsole.Plugins.RabbitMq.Client;

public enum GetMode
{
    /// <summary>basic.get, then basic.nack(requeue: true) -- nothing is removed, messages come back redelivered.</summary>
    Peek,

    /// <summary>basic.get, then basic.ack -- every message read is removed from the queue.</summary>
    Consume,
}

public enum PublishOutcome
{
    Routed,

    /// <summary>The broker returned the mandatory message (basic.return): no queue was bound for it.</summary>
    Unroutable,
}

public sealed record CreateExchangeRequest(
    string Name,
    string Type,
    bool Durable = true,
    bool AutoDelete = false,
    bool Internal = false,
    string? AlternateExchange = null);

public sealed record CreateQueueRequest(
    string Name,
    string Type = "classic",
    bool Durable = true,
    bool AutoDelete = false,
    string? DeadLetterExchange = null,
    string? DeadLetterRoutingKey = null,
    long? MessageTtlMs = null,
    long? MaxLength = null,
    string? Overflow = null);

/// <summary>
/// A dynamic shovel definition. Exactly one of DestinationQueue/DestinationExchange is set.
/// "amqp://" URIs with no host mean this broker; the vhost must be URI-encoded into them
/// (amqp:///%2Forders) -- ManagementApiClient builds them from the vhost when left null.
/// </summary>
public sealed record CreateShovelRequest(
    string Name,
    string SourceQueue,
    string? DestinationQueue,
    string? DestinationExchange,
    string? DestinationRoutingKey,
    string AckMode = "on-confirm",
    string? SourceUri = null,
    string? DestinationUri = null,
    string SourceDeleteAfter = "never");

/// <summary>
/// One AMQP publish. MessageId/CorrelationId/Timestamp let a republish carry the original's
/// identity. Exchange "" is the default exchange (routing key = queue name).
/// </summary>
public sealed record PublishRequest(
    string Exchange,
    string RoutingKey,
    byte[] Body,
    bool Persistent = true,
    int? Priority = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? MessageId = null,
    string? CorrelationId = null,
    DateTimeOffset? Timestamp = null,
    string? AppId = null);
