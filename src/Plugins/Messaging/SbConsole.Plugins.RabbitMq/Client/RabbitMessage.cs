namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// A message read by basic.get. Index is its position in this get (0-based, oldest first).
/// Headers are decoded to strings (AMQP carries header strings as byte[]); x-death is parsed into
/// Deaths rather than left in Headers. DeliveryCount is the quorum-queue x-delivery-count, when
/// present -- the one already on the message, not one a peek added.
/// </summary>
public sealed record RabbitMessage(
    int Index,
    string? MessageId,
    string? CorrelationId,
    string Exchange,
    string RoutingKey,
    bool Redelivered,
    string? ContentType,
    string? ContentEncoding,
    int DeliveryMode,
    int? Priority,
    string? AppId,
    DateTimeOffset? Timestamp,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    IReadOnlyList<DeathRecord> Deaths,
    string? FirstDeathQueue,
    string? FirstDeathExchange,
    long? DeliveryCount)
{
    public bool IsPersistent => DeliveryMode == 2;
}
