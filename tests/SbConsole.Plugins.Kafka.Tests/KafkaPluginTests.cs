using FluentAssertions;
using SbConsole.Plugins.Kafka;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests;

public class KafkaPluginTests
{
    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new KafkaPlugin();

        plugin.Id.Should().Be("kafka");
        plugin.ConnectionKind.Should().Be("kafka");
        plugin.DisplayName.Should().Be("Apache Kafka");
        plugin.ConnectionKindDisplayName.Should().Be("Apache Kafka");
        plugin.NavItems.Should().ContainSingle(n => n.Title == "Topics" && n.Href == "/p/kafka/topics");
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 2, ActionCount: 4));
    }

    [Fact]
    public async Task TestConnectionAsync_delegates_to_the_real_Kafka_client_and_never_throws()
    {
        var plugin = new KafkaPlugin();

        var result = await plugin.TestConnectionAsync("bootstrap.servers=127.0.0.1:1");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
