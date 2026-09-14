using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Subscriptions;

public class CreateSubscriptionCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_subscription_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateSubscriptionCommandHandler(operations, connections, audit, NullLogger<CreateSubscriptionCommandHandler>.Instance)
            .HandleAsync(new CreateSubscriptionCommand(connectionId, "sb-dev", "orders", "uk-team", 10));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateSubscriptionAsync("Endpoint=sb://real", "orders", Arg.Is<CreateSubscriptionRequest>(r => r.Name == "uk-team" && r.MaxDeliveryCount == 10), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("subscription.create", "sb-dev/orders/uk-team", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.CreateSubscriptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateSubscriptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateSubscriptionCommandHandler(operations, connections, audit, NullLogger<CreateSubscriptionCommandHandler>.Instance)
            .HandleAsync(new CreateSubscriptionCommand(connectionId, "sb-dev", "orders", "uk-team", 10));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already exists");
        await audit.Received(1).RecordAsync("subscription.create", "sb-dev/orders/uk-team", ActionRisk.Mutating, false, "already exists", Arg.Any<CancellationToken>());
    }
}
