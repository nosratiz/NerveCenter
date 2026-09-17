using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Rules;

public class EditRuleCommandHandlerTests
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public EditRuleCommandHandlerTests()
    {
        // Default return for any unconfigured connection
        _connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        // Override for the specific test connection
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
    }

    private EditRuleCommandHandler Handler => new(_operations, _connections, _audit, NullLogger<EditRuleCommandHandler>.Instance);

    [Fact]
    public async Task Same_name_edit_deletes_then_creates_and_audits_both()
    {
        var newRule = new CreateSqlRuleRequest("HighPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeTrue();
        await _operations.Received(1).DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
        await _operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", newRule, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_edit_creates_then_deletes_and_audits_both()
    {
        var newRule = new CreateSqlRuleRequest("HighestPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _operations.CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", newRule, Arg.Any<CancellationToken>());
            _operations.DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
        });
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighestPriority", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Same_name_edit_stops_and_reports_when_the_delete_fails()
    {
        _operations.DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule not found")));
        var newRule = new CreateSqlRuleRequest("HighPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("rule not found");
        await _operations.DidNotReceive().CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, false, "rule not found", Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().RecordAsync("rule.create", Arg.Any<string>(), Arg.Any<ActionRisk>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Same_name_edit_reports_the_stranded_rule_message_when_create_fails_after_delete_succeeds()
    {
        _operations.CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("quota exceeded")));
        var newRule = new CreateSqlRuleRequest("HighPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Rule 'HighPriority' was deleted but the update could not be created (quota exceeded) — it no longer exists and must be re-added.");
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Mutating, false, "quota exceeded", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_edit_stops_and_reports_when_the_create_fails()
    {
        _operations.CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("a rule with this name already exists")));
        var newRule = new CreateSqlRuleRequest("HighestPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("a rule with this name already exists");
        await _operations.DidNotReceive().DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighestPriority", ActionRisk.Mutating, false, "a rule with this name already exists", Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().RecordAsync("rule.delete", Arg.Any<string>(), Arg.Any<ActionRisk>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_edit_reports_the_manual_cleanup_message_when_delete_fails_after_create_succeeds()
    {
        _operations.DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule not found")));
        var newRule = new CreateSqlRuleRequest("HighestPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(_connectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("A new rule 'HighestPriority' was created, but the old rule 'HighPriority' could not be removed (rule not found) and must be deleted manually.");
        await _audit.Received(1).RecordAsync("rule.create", "sb-dev/orders/uk-team/HighestPriority", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("rule.delete", "sb-dev/orders/uk-team/HighPriority", ActionRisk.Destructive, false, "rule not found", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_connection_fails_without_calling_operations()
    {
        var missingConnectionId = Guid.NewGuid();
        var newRule = new CreateSqlRuleRequest("HighPriority", "Priority = 'Highest'");

        var result = await Handler.HandleAsync(new EditRuleCommand(missingConnectionId, "sb-dev", false, "orders", "uk-team", "HighPriority", newRule));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await _operations.DidNotReceive().DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>());
    }
}
