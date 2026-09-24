using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class ListQueueSnsSubscriptionsQueryHandlerTests
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISnsOperations _operations = Substitute.For<ISnsOperations>();

    public ListQueueSnsSubscriptionsQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
    }

    private ListQueueSnsSubscriptionsQueryHandler Handler() =>
        new(_operations, _connections, NullLogger<ListQueueSnsSubscriptionsQueryHandler>.Instance);

    [Fact]
    public async Task Returns_subscriptions_whose_endpoint_is_the_queue_arn()
    {
        var subs = new List<SubscriptionSummary>
        {
            new("arn:sub-1", "sqs", "arn:aws:sqs:eu-west-1:1:orders", false, null, null, "arn:aws:sns:eu-west-1:1:order-events"),
        };
        _operations.ListSubscriptionsForEndpointAsync("mode=default-chain;region=eu-west-1", "arn:aws:sqs:eu-west-1:1:orders", Arg.Any<CancellationToken>())
            .Returns(subs);

        var result = await Handler().HandleAsync(_connectionId, "arn:aws:sqs:eu-west-1:1:orders");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(subs);
    }

    [Fact]
    public async Task Unknown_connection_fails()
    {
        var unknownId = Guid.NewGuid();
        _connections.GetSecretAsync(unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(unknownId, "arn:aws:sqs:eu-west-1:1:orders");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
    }

    [Fact]
    public async Task Operation_failure_is_reduced_to_a_friendly_error()
    {
        _operations.ListSubscriptionsForEndpointAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<SubscriptionSummary>>(new InvalidOperationException("AuthorizationError")));

        var result = await Handler().HandleAsync(_connectionId, "arn:aws:sqs:eu-west-1:1:orders");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }
}
