using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Subscriptions;

public class DeleteSubscriptionCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_subscription_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteSubscriptionCommandHandler(operations, connections, audit, NullLogger<DeleteSubscriptionCommandHandler>.Instance)
            .HandleAsync(new DeleteSubscriptionCommand(connectionId, "sb-dev", false, "orders", "uk-team"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteSubscriptionAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("subscription.delete", "sb-dev/orders/uk-team", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
