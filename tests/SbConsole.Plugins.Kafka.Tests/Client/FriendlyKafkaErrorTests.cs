using Confluent.Kafka;
using FluentAssertions;
using SbConsole.Plugins.Kafka.Client;

namespace SbConsole.Plugins.Kafka.Tests.Client;

public class FriendlyKafkaErrorTests
{
    [Theory]
    [InlineData(ErrorCode.Local_AllBrokersDown, "Broker(s) unreachable")]
    [InlineData(ErrorCode.Local_Transport, "Broker(s) unreachable")]
    [InlineData(ErrorCode.SaslAuthenticationFailed, "Authentication failed")]
    [InlineData(ErrorCode.TopicAuthorizationFailed, "Authentication failed")]
    [InlineData(ErrorCode.UnknownTopicOrPart, "Topic not found")]
    public void Known_error_codes_map_to_a_fixed_readable_message(ErrorCode code, string expected)
    {
        var ex = new KafkaException(new Error(code, "raw librdkafka reason text that must never reach the UI"));

        FriendlyKafkaError.From(ex).Should().Be(expected);
    }

    [Fact]
    public void Unmapped_error_codes_fall_back_to_the_librdkafka_reason_text()
    {
        var ex = new KafkaException(new Error(ErrorCode.Unknown, "some other librdkafka reason"));

        FriendlyKafkaError.From(ex).Should().Be("some other librdkafka reason");
    }

    [Fact]
    public void Non_Kafka_exceptions_fall_back_to_the_shared_FriendlyError_helper()
    {
        var ex = new InvalidOperationException("plain failure");

        FriendlyKafkaError.From(ex).Should().Be("plain failure");
    }
}
