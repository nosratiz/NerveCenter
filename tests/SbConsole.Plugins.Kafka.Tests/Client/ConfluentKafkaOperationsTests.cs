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
        // ClientConfig.SecurityProtocol getter maps the raw string to the enum via
        // TextInfo.ToTitleCase per underscore-segment, and ToTitleCase leaves an all-caps segment
        // ("SASL", "SSL") untouched as if it were an acronym, so "SASL_SSL" fails to parse against
        // the actually-installed Confluent.Kafka 2.15.1 -- verified empirically against the real
        // package, not assumed.
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
}
