using FluentAssertions;
using SbConsole.Plugins.ServiceBus;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests;

public class ServiceBusPluginTests
{
    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new ServiceBusPlugin();

        plugin.Id.Should().Be("azure-servicebus");
        plugin.ConnectionKind.Should().Be("azure-servicebus");
        plugin.DisplayName.Should().Be("Azure Service Bus");
        plugin.ConnectionKindDisplayName.Should().Be("Azure Service Bus");
        plugin.NavItems.Should().ContainSingle(n => n.Title == "Queues" && n.Href == "/p/azure-servicebus/queues");
    }

    [Fact]
    public async Task TestConnectionAsync_delegates_to_the_real_Azure_SDK_and_never_throws()
    {
        var plugin = new ServiceBusPlugin();

        var result = await plugin.TestConnectionAsync("not-a-real-connection-string");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
