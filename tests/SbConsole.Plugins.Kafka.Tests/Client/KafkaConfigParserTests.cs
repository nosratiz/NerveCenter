using FluentAssertions;
using SbConsole.Plugins.Kafka.Client;

namespace SbConsole.Plugins.Kafka.Tests.Client;

public class KafkaConfigParserTests
{
    [Fact]
    public void Empty_string_parses_to_an_empty_dictionary()
    {
        KafkaConfigParser.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void Parses_a_single_key_value_pair()
    {
        KafkaConfigParser.Parse("bootstrap.servers=broker1:9092")
            .Should().Equal(new Dictionary<string, string> { ["bootstrap.servers"] = "broker1:9092" });
    }

    [Fact]
    public void Parses_multiple_pairs_separated_by_semicolons()
    {
        var result = KafkaConfigParser.Parse("bootstrap.servers=broker1:9092;security.protocol=SASL_SSL;sasl.mechanism=PLAIN");

        result.Should().Equal(new Dictionary<string, string>
        {
            ["bootstrap.servers"] = "broker1:9092",
            ["security.protocol"] = "SASL_SSL",
            ["sasl.mechanism"] = "PLAIN",
        });
    }

    [Fact]
    public void Ignores_a_trailing_semicolon()
    {
        KafkaConfigParser.Parse("bootstrap.servers=broker1:9092;")
            .Should().Equal(new Dictionary<string, string> { ["bootstrap.servers"] = "broker1:9092" });
    }

    [Fact]
    public void Skips_a_malformed_segment_with_no_equals_sign()
    {
        KafkaConfigParser.Parse("bootstrap.servers=broker1:9092;not-a-pair;security.protocol=PLAINTEXT")
            .Should().Equal(new Dictionary<string, string>
            {
                ["bootstrap.servers"] = "broker1:9092",
                ["security.protocol"] = "PLAINTEXT",
            });
    }

    [Fact]
    public void Later_duplicate_keys_win()
    {
        KafkaConfigParser.Parse("sasl.username=first;sasl.username=second")
            .Should().Equal(new Dictionary<string, string> { ["sasl.username"] = "second" });
    }

    [Fact]
    public void Trims_whitespace_around_keys_and_values()
    {
        KafkaConfigParser.Parse(" bootstrap.servers = broker1:9092 ; security.protocol = PLAINTEXT ")
            .Should().Equal(new Dictionary<string, string>
            {
                ["bootstrap.servers"] = "broker1:9092",
                ["security.protocol"] = "PLAINTEXT",
            });
    }
}
