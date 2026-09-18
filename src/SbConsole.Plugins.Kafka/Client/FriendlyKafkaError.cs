using Confluent.Kafka;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Kafka-specific counterpart to SbConsole.Sdk.FriendlyError: maps the ErrorCodes this plugin's
/// operations can actually hit to a short, fixed, readable message, falling back to the
/// (capped/collapsed) librdkafka reason text for anything unmapped. Confluent.Kafka failures
/// surface as KafkaException or its subclass ProduceException&lt;TKey,TValue&gt; -- both match the
/// KafkaException case below since ProduceException IS-A KafkaException. Verified against the
/// installed Confluent.Kafka version's actual ErrorCode enum at implementation time, same
/// "confirmed, not assumed" bar the Service Bus plugin's error mapping holds itself to.
/// </summary>
public static class FriendlyKafkaError
{
    public static string From(Exception ex) => ex switch
    {
        KafkaException kex => FromKafkaException(kex),
        _ => FriendlyError.From(ex),
    };

    private static string FromKafkaException(KafkaException ex) => ex.Error.Code switch
    {
        ErrorCode.Local_AllBrokersDown or ErrorCode.Local_Transport or ErrorCode.BrokerNotAvailable => "Broker(s) unreachable",
        ErrorCode.SaslAuthenticationFailed or ErrorCode.TopicAuthorizationFailed => "Authentication failed",
        ErrorCode.UnknownTopicOrPart => "Topic not found",
        ErrorCode.GroupIdNotFound => "Consumer group not found",
        ErrorCode.NonEmptyGroup or ErrorCode.UnknownMemberId or ErrorCode.RebalanceInProgress =>
            "Consumer group has active members; wait until it is idle before resetting offsets",
        _ => FriendlyError.Truncate(ex.Error.Reason) is { Length: > 0 } reason ? reason : FriendlyError.From(ex),
    };
}
