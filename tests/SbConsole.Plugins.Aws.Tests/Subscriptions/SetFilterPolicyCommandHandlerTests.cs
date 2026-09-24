using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class SetFilterPolicyCommandHandlerTests
{
    private const string Secret = "mode=access-keys;region=us-east-1";
    private readonly ISnsOperations _operations = Substitute.For<ISnsOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public SetFilterPolicyCommandHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    private SetFilterPolicyCommandHandler CreateHandler() =>
        new(_operations, _connections, _audit, NullLogger<SetFilterPolicyCommandHandler>.Instance);

    private SetFilterPolicyCommand Command(string? policy, string scope = FilterPolicyValidator.MessageAttributes) =>
        new(_connectionId, "aws-dev", "orders-topic", "arn:sub-1", policy, scope);

    [Fact]
    public async Task Sets_the_policy_and_audits_as_mutating()
    {
        var result = await CreateHandler().HandleAsync(Command("""{"region":["uk"]}""", FilterPolicyValidator.MessageBody));

        result.IsSuccess.Should().BeTrue();
        await _operations.Received(1).SetSubscriptionFilterPolicyAsync(Secret, "arn:sub-1", """{"region":["uk"]}""", FilterPolicyValidator.MessageBody, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("aws.subscription.filterpolicy.set", "aws-dev/orders-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_policy_clears_it_by_passing_null()
    {
        var result = await CreateHandler().HandleAsync(Command("  "));

        result.IsSuccess.Should().BeTrue();
        await _operations.Received(1).SetSubscriptionFilterPolicyAsync(Secret, "arn:sub-1", null, FilterPolicyValidator.MessageAttributes, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("aws.subscription.filterpolicy.set", "aws-dev/orders-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_non_object_json_without_calling_aws()
    {
        var result = await CreateHandler().HandleAsync(Command("""["uk"]"""));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("JSON object");
        await _operations.DidNotReceiveWithAnyArgs().SetSubscriptionFilterPolicyAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task A_failure_is_friendly_and_audited_as_failed()
    {
        _operations.SetSubscriptionFilterPolicyAsync(Secret, "arn:sub-1", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Invalid parameter: FilterPolicy"));

        var result = await CreateHandler().HandleAsync(Command("""{"region":["uk"]}"""));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Invalid parameter: FilterPolicy");
        await _audit.Received(1).RecordAsync("aws.subscription.filterpolicy.set", "aws-dev/orders-topic", ActionRisk.Mutating, false, Arg.Is<string?>(d => d != null && d.Contains("FilterPolicy")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fails_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateHandler().HandleAsync(Command("""{"region":["uk"]}"""));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
    }
}
