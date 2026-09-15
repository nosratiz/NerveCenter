using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Rules;

public class DeleteRuleCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_rule_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteRuleCommandHandler(operations, connections, audit, NullLogger<DeleteRuleCommandHandler>.Instance)
            .HandleAsync(new DeleteRuleCommand(connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule not found")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteRuleCommandHandler(operations, connections, audit, NullLogger<DeleteRuleCommandHandler>.Instance)
            .HandleAsync(new DeleteRuleCommand(connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("rule not found");
        await audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, false, "rule not found", Arg.Any<CancellationToken>());
    }
}
