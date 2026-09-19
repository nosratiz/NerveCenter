using Confluent.Kafka;
using FluentAssertions;
using SbConsole.Plugins.Kafka.Client;

namespace SbConsole.Plugins.Kafka.Tests.Client;

public class ConfluentKafkaOperationsTests
{
    // What's testable here without a broker is exactly the client-side half: building the typed
    // config objects from a parsed connection string. Every network-touching branch (unreachable
    // broker, auth failure) is out of scope for this suite per the design spec §8 -- same
    // "no real AMQP/HTTP traffic" rule AzureServiceBusOperationsTests follows.

    [Fact]
    public void CreateAdminClientConfig_wraps_the_parsed_dictionary()
    {
        // Lowercase "sasl_ssl" (the librdkafka-canonical form), not "SASL_SSL": Confluent.Kafka's
        // ClientConfig.SecurityProtocol getter (Config.GetEnum) first does a case-sensitive lookup
        // of the raw string against a table of enum-name-to-canonical-value substitutes (e.g.
        // "saslssl" -> "sasl_ssl") and, only on a match, parses the substitute's *key* via
        // Enum.Parse(ignoreCase: true) -- lowercase "sasl_ssl" matches that table and resolves to
        // SecurityProtocol.SaslSsl. Uppercase "SASL_SSL" would miss the case-sensitive table lookup
        // and fall through to Enum.Parse(type, "SASL_SSL", ignoreCase: true) directly, which throws
        // because the enum member name ("SaslSsl") has no underscore for any casing to match.
        // Verified via decompilation of the actually-installed Confluent.Kafka 2.15.1, not assumed
        // (this is not TextInfo.ToTitleCase, which was an earlier, incorrect guess at the mechanism).
        var config = ConfluentKafkaOperations.CreateAdminClientConfig("bootstrap.servers=broker1:9092;security.protocol=sasl_ssl");

        config.BootstrapServers.Should().Be("broker1:9092");
        config.SecurityProtocol.Should().Be(SecurityProtocol.SaslSsl);
    }

    [Fact]
    public void CreateConsumerConfig_sets_the_given_group_id_and_disables_auto_commit()
    {
        var config = ConfluentKafkaOperations.CreateConsumerConfig("bootstrap.servers=broker1:9092", "my-group");

        config.BootstrapServers.Should().Be("broker1:9092");
        config.GroupId.Should().Be("my-group");
        config.EnableAutoCommit.Should().BeFalse();
    }

    [Fact]
    public void CreateProducerConfig_wraps_the_parsed_dictionary()
    {
        var config = ConfluentKafkaOperations.CreateProducerConfig("bootstrap.servers=broker1:9092");

        config.BootstrapServers.Should().Be("broker1:9092");
    }

    [Fact]
    public void CreateProducerConfig_bounds_message_and_socket_timeouts_to_AttemptTimeout()
    {
        // Regression guard for the "produce hangs 5 minutes against an unreachable cluster" bug:
        // without these, message.timeout.ms/socket.timeout.ms stay at librdkafka's defaults
        // (300000ms / 60000ms) instead of failing fast.
        var config = ConfluentKafkaOperations.CreateProducerConfig("bootstrap.servers=broker1:9092");

        config.MessageTimeoutMs.Should().Be((int)ConfluentKafkaOperations.AttemptTimeout.TotalMilliseconds);
        config.SocketTimeoutMs.Should().Be((int)ConfluentKafkaOperations.AttemptTimeout.TotalMilliseconds);
    }

    [Fact]
    public void BuildCreateTopicsOptions_sets_a_request_timeout_bounded_by_AttemptTimeout()
    {
        var options = ConfluentKafkaOperations.BuildCreateTopicsOptions();

        options.RequestTimeout.Should().Be(ConfluentKafkaOperations.AttemptTimeout);
    }

    [Fact]
    public void BuildDeleteTopicsOptions_sets_a_request_timeout_bounded_by_AttemptTimeout()
    {
        var options = ConfluentKafkaOperations.BuildDeleteTopicsOptions();

        options.RequestTimeout.Should().Be(ConfluentKafkaOperations.AttemptTimeout);
    }

    // Regression guard for the "poll timeout treated as end-of-partition" bug: Consume(TimeSpan)
    // returns null on a plain poll timeout, which the peek loop must `continue` past (bounded by
    // PeekWallClockCap), not `break` on -- only a genuine IsPartitionEOF result should stop the
    // loop. ConsumeResult<TKey,TValue> is a plain settable POCO, so these three shapes are
    // constructible directly, without a real broker.
    [Fact]
    public void IsEndOfPartition_is_false_for_a_null_result_ie_a_plain_poll_timeout()
    {
        ConfluentKafkaOperations.IsEndOfPartition(null).Should().BeFalse();
    }

    [Fact]
    public void IsEndOfPartition_is_true_only_when_the_result_flags_end_of_partition()
    {
        var eof = new Confluent.Kafka.ConsumeResult<byte[], byte[]> { IsPartitionEOF = true };

        ConfluentKafkaOperations.IsEndOfPartition(eof).Should().BeTrue();
    }

    [Fact]
    public void IsEndOfPartition_is_false_for_a_real_message_result()
    {
        var message = new Confluent.Kafka.ConsumeResult<byte[], byte[]>
        {
            IsPartitionEOF = false,
            Message = new Confluent.Kafka.Message<byte[], byte[]> { Value = System.Text.Encoding.UTF8.GetBytes("hello") },
        };

        ConfluentKafkaOperations.IsEndOfPartition(message).Should().BeFalse();
    }

    [Fact]
    public void Valid_UTF8_bytes_decode_as_text()
    {
        var (text, isBase64) = ConfluentKafkaOperations.Decode(System.Text.Encoding.UTF8.GetBytes("hello world"));

        text.Should().Be("hello world");
        isBase64.Should().BeFalse();
    }

    [Fact]
    public void Non_UTF8_bytes_decode_as_base64()
    {
        byte[] invalidUtf8 = [0xFF, 0xFE, 0x00, 0x01];

        var (text, isBase64) = ConfluentKafkaOperations.Decode(invalidUtf8);

        isBase64.Should().BeTrue();
        text.Should().Be(Convert.ToBase64String(invalidUtf8));
    }

    [Theory]
    [InlineData(90, 100, 10)]   // normal case: 10 messages behind
    [InlineData(100, 100, 0)]   // caught up
    [InlineData(105, 100, 0)]   // committed briefly ahead of a just-moved watermark -- clamped, not negative
    public void ComputeLag_clamps_to_zero_and_never_returns_negative(long committedOffset, long highWatermark, long expected)
    {
        ConfluentKafkaOperations.ComputeLag(committedOffset, highWatermark).Should().Be(expected);
    }

    [Fact]
    public void ResolveTimestampLookupResult_falls_back_to_the_high_watermark_when_no_message_exists_at_or_after_the_timestamp()
    {
        // Kafka's ListOffsets protocol returns -1 when no message exists at/after the requested
        // timestamp -- Kafka's own "not found" sentinel (distinct from, though numerically equal
        // to, librdkafka's Offset.End constant). See design spec §4. The fallback must be a real,
        // resolved offset (the high watermark), not the Offset.End sentinel itself --
        // AlterConsumerGroupOffsetsAsync rejects sentinel values with "offset must be >= 0",
        // confirmed against a real broker.
        ConfluentKafkaOperations.ResolveTimestampLookupResult(-1, highWatermarkFallback: 777).Should().Be(new Offset(777));
    }

    [Fact]
    public void ResolveTimestampLookupResult_returns_the_resolved_offset_when_a_message_was_found()
    {
        ConfluentKafkaOperations.ResolveTimestampLookupResult(4242, highWatermarkFallback: 777).Should().Be(new Offset(4242));
    }

    [Theory]
    [InlineData("orders-dlq", true)]
    [InlineData("orders", false)]
    [InlineData("orders-dlq-archive", false)]  // "-dlq" not at the end -- must not match
    [InlineData("-dlq", true)]                  // degenerate but valid: empty original name
    public void IsDlqTopic_matches_only_the_dash_dlq_suffix(string name, bool expected)
    {
        ConfluentKafkaOperations.IsDlqTopic(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("orders-dlq", "orders")]
    [InlineData("-dlq", "")]
    public void OriginalTopicName_strips_the_dash_dlq_suffix(string dlqTopicName, string expected)
    {
        ConfluentKafkaOperations.OriginalTopicName(dlqTopicName).Should().Be(expected);
    }
}
