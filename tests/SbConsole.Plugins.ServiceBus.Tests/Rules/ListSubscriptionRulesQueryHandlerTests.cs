using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Rules;

public class ListSubscriptionRulesQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_subscriptions_rules()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new("HighPriority", "Priority = 'High'") });

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeTrue();
        var rule = result.Value.Should().ContainSingle().Subject;
        rule.Name.Should().Be("HighPriority");
        rule.SqlExpression.Should().Be("Priority = 'High'");
    }

    [Fact]
    public async Task Missing_connection_fails_without_calling_operations()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        var operations = Substitute.For<IServiceBusOperations>();

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await operations.DidNotReceive().ListRulesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_through_FriendlyError()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListRulesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<RuleSummary>>(new InvalidOperationException("subscription not found")));

        var result = await new ListSubscriptionRulesQueryHandler(operations, connections, NullLogger<ListSubscriptionRulesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("subscription not found");
    }
}
