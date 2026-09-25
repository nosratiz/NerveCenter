using FluentAssertions;
using SbConsole.Plugins.RabbitMq;

namespace SbConsole.Plugins.RabbitMq.Tests;

public class RabbitMqPluginTests
{
    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new RabbitMqPlugin();

        plugin.Id.Should().Be("rabbitmq");
        plugin.ConnectionKind.Should().Be("rabbitmq");
        plugin.DisplayName.Should().Be("RabbitMQ");
        plugin.ConnectionKindDisplayName.Should().Be("RabbitMQ");
        plugin.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void Declares_the_four_nav_items_in_mockup_order()
    {
        var plugin = new RabbitMqPlugin();

        plugin.NavItems.Select(n => (n.Title, n.Href)).Should().Equal(
            ("Overview", "/p/rabbitmq/overview"),
            ("Exchanges", "/p/rabbitmq/exchanges"),
            ("Queues", "/p/rabbitmq/queues"),
            ("Shovels & policies", "/p/rabbitmq/shovels"));
    }

    [Fact]
    public async Task TestConnectionAsync_is_a_non_throwing_placeholder_until_the_operations_seam_exists()
    {
        var plugin = new RabbitMqPlugin();

        var result = await plugin.TestConnectionAsync("host=localhost;username=guest;password=guest");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Not implemented");
    }
}
