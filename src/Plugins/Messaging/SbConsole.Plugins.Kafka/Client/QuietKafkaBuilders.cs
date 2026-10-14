using Confluent.Kafka;

namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Swallows librdkafka's own log and error callbacks. Without a handler, librdkafka prints every
/// FAIL/ERROR line straight to the process's native stderr -- against a stopped broker that is
/// several lines per client roughly every 50ms for the whole AttemptTimeout, drowning the host's
/// logs (the Dashboard, Wallboard and nav badge each open clients on every refresh). Nothing is
/// lost: every failure still surfaces as a KafkaException / failed ConnectionTestResult, which the
/// callers log once through ILogger with a friendly message.
/// </summary>
internal static class QuietKafkaBuilders
{
    public static AdminClientBuilder Quiet(this AdminClientBuilder builder) =>
        builder.SetLogHandler(static (_, _) => { }).SetErrorHandler(static (_, _) => { });

    public static ConsumerBuilder<TKey, TValue> Quiet<TKey, TValue>(this ConsumerBuilder<TKey, TValue> builder) =>
        builder.SetLogHandler(static (_, _) => { }).SetErrorHandler(static (_, _) => { });

    public static ProducerBuilder<TKey, TValue> Quiet<TKey, TValue>(this ProducerBuilder<TKey, TValue> builder) =>
        builder.SetLogHandler(static (_, _) => { }).SetErrorHandler(static (_, _) => { });
}
