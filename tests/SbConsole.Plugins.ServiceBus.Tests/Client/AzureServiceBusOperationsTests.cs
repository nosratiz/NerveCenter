using FluentAssertions;
using SbConsole.Plugins.ServiceBus.Client;

namespace SbConsole.Plugins.ServiceBus.Tests.Client;

public class AzureServiceBusOperationsTests
{
    [Fact]
    public async Task TestConnectionAsync_returns_a_readable_failure_for_a_malformed_connection_string()
    {
        var operations = new AzureServiceBusOperations();

        var result = await operations.TestConnectionAsync("this-is-not-a-real-service-bus-connection-string");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TestConnectionAsync_never_throws_regardless_of_input()
    {
        var operations = new AzureServiceBusOperations();

        var act = async () => await operations.TestConnectionAsync("");

        await act.Should().NotThrowAsync();
    }
}
