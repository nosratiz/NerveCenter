using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class RabbitConfigParserTests
{
    [Fact]
    public void Round_trips_values_containing_separator_characters_and_a_slash_vhost()
    {
        var fields = new Dictionary<string, string>
        {
            ["host"] = "rabbit-01.uk.internal",
            ["amqpPort"] = "5671",
            ["managementUrl"] = "https://rabbit-01.uk.internal:15671",
            ["vhost"] = "/orders",
            ["username"] = "sbconsole",
            ["password"] = "p;a=s%s w0rd",
            ["tls"] = "true",
            ["verifyCert"] = "true",
        };

        var secret = RabbitConfigParser.Serialize(fields);
        var parsed = RabbitConfigParser.Parse(secret);

        parsed.Should().BeEquivalentTo(fields);
    }

    [Fact]
    public void Serialize_percent_encodes_every_value_in_a_fixed_key_order_and_skips_empty_values()
    {
        var fields = new Dictionary<string, string>
        {
            ["password"] = "a;b",
            ["vhost"] = "/orders",
            ["host"] = "h",
            ["managementUrl"] = "",
            ["unknown"] = "dropped",
        };

        RabbitConfigParser.Serialize(fields).Should().Be("host=h;vhost=%2Forders;password=a%3Bb");
    }

    [Fact]
    public void Parse_skips_malformed_segments_and_splits_on_the_first_equals_only()
    {
        var parsed = RabbitConfigParser.Parse("host=h;garbage;=novalue;username=u;password=a=b");

        parsed.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["host"] = "h",
            ["username"] = "u",
            ["password"] = "a=b",
        });
    }

    [Fact]
    public void Parse_keeps_the_raw_value_when_an_escape_sequence_is_malformed()
    {
        var parsed = RabbitConfigParser.Parse("password=100%zz");

        parsed["password"].Should().Be("100%zz");
    }

    [Fact]
    public void SafeEcho_lists_only_allowlisted_keys_and_never_the_password()
    {
        var secret = "host=h;amqpPort=5672;managementUrl=http%3A%2F%2Fh%3A15672;vhost=%2Forders;username=u;password=hunter2;tls=false";

        var echo = RabbitConfigParser.SafeEcho(secret);

        echo.Should().Be("host=h · amqpPort=5672 · managementUrl=http://h:15672 · vhost=/orders · username=u");
        echo.Should().NotContain("hunter2");
        echo.Should().NotContain("password");
    }
}
