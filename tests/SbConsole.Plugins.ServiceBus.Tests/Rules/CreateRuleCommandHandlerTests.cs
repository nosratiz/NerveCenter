using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Rules;

public class CreateRuleCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_rule_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateRuleCommandHandler(operations, connections, audit, NullLogger<CreateRuleCommandHandler>.Instance)
            .HandleAsync(new CreateRuleCommand(connectionId, "sb-dev", "orders", "uk-team", new CreateSqlRuleRequest("HighPriority", "Priority = 'High'")));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Is<CreateRuleRequest>(r => r is CreateSqlRuleRequest && ((CreateSqlRuleRequest)r).Name == "HighPriority" && ((CreateSqlRuleRequest)r).SqlExpression == "Priority = 'High'"), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateRuleCommandHandler(operations, connections, audit, NullLogger<CreateRuleCommandHandler>.Instance)
            .HandleAsync(new CreateRuleCommand(connectionId, "sb-dev", "orders", "uk-team", new CreateSqlRuleRequest("HighPriority", "Priority = 'High'")));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("rule already exists");
        await audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Mutating, false, "rule already exists", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creates_a_correlation_rule_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();
        var request = new CreateCorrelationRuleRequest("VipCustomers", new CorrelationMatch(CorrelationId: "vip-123", Label: "Orders"), new Dictionary<string, string> { ["tier"] = "gold" });

        var result = await new CreateRuleCommandHandler(operations, connections, audit, NullLogger<CreateRuleCommandHandler>.Instance)
            .HandleAsync(new CreateRuleCommand(connectionId, "sb-dev", "orders", "uk-team", request));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", request, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/VipCustomers", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
