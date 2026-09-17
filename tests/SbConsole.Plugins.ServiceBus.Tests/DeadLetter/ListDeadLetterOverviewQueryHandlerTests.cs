using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.DeadLetter;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.DeadLetter;

public class ListDeadLetterOverviewQueryHandlerTests
{
    [Fact]
    public async Task Merges_entries_across_connections_and_tags_each_with_its_connection()
    {
        var connectionA = Guid.NewGuid();
        var connectionB = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionA, "sb-dev", "azure-servicebus", []), new(connectionB, "sb-uk-prod", "azure-servicebus", ["prod"]) });
        connections.GetSecretAsync(connectionA, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://dev");
        connections.GetSecretAsync(connectionB, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://prod");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListDeadLetterEntriesAsync("Endpoint=sb://dev", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterEntry> { new("Queue", null, "orders-inbound", 3) });
        operations.ListDeadLetterEntriesAsync("Endpoint=sb://prod", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterEntry> { new("Subscription", "orders", "uk-team", 11) });

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().HaveCount(2);
        result.Should().ContainSingle(e => e.ConnectionName == "sb-dev" && e.EntityName == "orders-inbound" && e.Count == 3);
        result.Should().ContainSingle(e => e.ConnectionName == "sb-uk-prod" && e.TopicName == "orders" && e.EntityName == "uk-team" && e.Count == 11);
    }

    [Fact]
    public async Task A_failing_connection_is_skipped_not_fatal_to_the_rest()
    {
        var connectionA = Guid.NewGuid();
        var connectionB = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionA, "sb-broken", "azure-servicebus", []), new(connectionB, "sb-ok", "azure-servicebus", []) });
        connections.GetSecretAsync(connectionA, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://broken");
        connections.GetSecretAsync(connectionB, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://ok");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListDeadLetterEntriesAsync("Endpoint=sb://broken", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<DeadLetterEntry>>(new InvalidOperationException("unreachable")));
        operations.ListDeadLetterEntriesAsync("Endpoint=sb://ok", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterEntry> { new("Queue", null, "orders-inbound", 3) });

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().ContainSingle(e => e.ConnectionName == "sb-ok");
    }

    [Fact]
    public async Task A_connection_with_a_missing_secret_is_skipped_not_fatal()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "sb-dev", "azure-servicebus", []) });
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListDeadLetterOverviewQueryHandler(Substitute.For<IServiceBusOperations>(), connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().BeEmpty();
    }
}
