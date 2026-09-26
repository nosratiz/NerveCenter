using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.RabbitMq.Client;
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
    public async Task TestConnectionAsync_reports_an_unparseable_secret_without_throwing()
    {
        var plugin = new RabbitMqPlugin();

        var result = await plugin.TestConnectionAsync("username=guest;password=guest");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Connection is missing 'host'.");
    }

    [Fact]
    public void ConfigureServices_registers_the_operations_seam_as_a_singleton()
    {
        var services = new ServiceCollection();

        new RabbitMqPlugin().ConfigureServices(services);

        services.Should().ContainSingle(d => d.ServiceType == typeof(IRabbitOperations))
            .Which.Should().Match<ServiceDescriptor>(d =>
                d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType == typeof(RabbitOperations));
    }

    [Fact]
    public void Uses_the_structured_connection_form()
    {
        new RabbitMqPlugin().ConnectionFormComponentType.Should().Be(typeof(RabbitConnectionFields));
    }

    [Fact]
    public void Summary_shows_host_and_vhost_but_never_credentials()
    {
        var summary = new RabbitMqPlugin().GetConnectionSummary(
            "host=rabbit.internal;vhost=%2Forders;username=svc;password=s3cret");

        summary.Should().BeEquivalentTo(new Dictionary<string, string> { ["Host"] = "rabbit.internal", ["Vhost"] = "/orders" });
        summary.Values.Should().NotContain(v => v.Contains("s3cret") || v.Contains("svc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage;;=;no-equals;%zz")]
    public void Summary_of_a_malformed_secret_defaults_without_throwing(string secret)
    {
        var summary = new RabbitMqPlugin().GetConnectionSummary(secret);

        summary.Should().BeEquivalentTo(new Dictionary<string, string> { ["Host"] = "?", ["Vhost"] = "/" });
    }
}
