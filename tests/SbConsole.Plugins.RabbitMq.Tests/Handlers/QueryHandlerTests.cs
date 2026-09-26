using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Connections;
using SbConsole.Plugins.RabbitMq.Exchanges;
using SbConsole.Plugins.RabbitMq.Messages;
using SbConsole.Plugins.RabbitMq.Overview;
using SbConsole.Plugins.RabbitMq.Policies;
using SbConsole.Plugins.RabbitMq.Queues;
using SbConsole.Plugins.RabbitMq.Shovels;

namespace SbConsole.Plugins.RabbitMq.Tests.Handlers;

public class QueryHandlerTests : HandlerTestBase
{
    [Fact]
    public async Task Echo_returns_the_safe_echo_and_the_configured_vhost()
    {
        var result = await new GetConnectionEchoQueryHandler(Connections, Log<GetConnectionEchoQueryHandler>()).HandleAsync(ConnectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.DefaultVhost.Should().Be("/orders");
        result.Value.Echo.Should().Contain("host=rabbit").And.NotContain("password");
    }

    [Fact]
    public async Task Echo_of_an_unparseable_secret_fails_with_the_settings_message()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns("vhost=%2F");

        var result = await new GetConnectionEchoQueryHandler(Connections, Log<GetConnectionEchoQueryHandler>()).HandleAsync(ConnectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("host");
    }

    [Fact]
    public async Task A_missing_connection_fails_without_calling_the_broker()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListVhostsQueryHandler(Operations, Connections, Log<ListVhostsQueryHandler>()).HandleAsync(ConnectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
        await Operations.DidNotReceiveWithAnyArgs().ListVhostsAsync(default!);
    }

    [Fact]
    public async Task A_management_failure_becomes_the_friendly_message()
    {
        Operations.ListVhostsAsync(Secret, Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden);

        var result = await new ListVhostsQueryHandler(Operations, Connections, Log<ListVhostsQueryHandler>()).HandleAsync(ConnectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ForbiddenText);
    }

    [Fact]
    public async Task Overview_combines_overview_nodes_and_the_vhost_queues()
    {
        var overview = new BrokerOverview("rabbit@local", "3.13.7", null, 1, 1, 1, 0, 2, 2, 10, 15, 1, 5, 0, 1, 1, 1);
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(overview);
        Operations.ListNodesAsync(Secret, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListQueuesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([Queue("q", 3)]);

        var result = await new GetOverviewQueryHandler(Operations, Connections, Log<GetOverviewQueryHandler>()).HandleAsync(ConnectionId, Vhost);

        result.Value!.Overview.Should().Be(overview);
        result.Value.Queues.Should().ContainSingle(q => q.Name == "q");
    }

    [Fact]
    public async Task Any_failed_read_fails_the_whole_snapshot()
    {
        Operations.ListExchangesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListBindingsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden);
        Operations.ListQueuesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new ListExchangesQueryHandler(Operations, Connections, Log<ListExchangesQueryHandler>()).HandleAsync(ConnectionId, Vhost);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(ForbiddenText);
    }

    [Fact]
    public async Task Exchange_bindings_are_filtered_to_the_source_exchange()
    {
        Operations.ListBindingsAsync(Secret, Vhost, Arg.Any<CancellationToken>())
            .Returns([Binding("order-events", "a", "order.*.created"), Binding("audit.fanout", "b")]);

        var result = await new ListExchangeBindingsQueryHandler(Operations, Connections, Log<ListExchangeBindingsQueryHandler>())
            .HandleAsync(ConnectionId, Vhost, "order-events");

        result.Value.Should().ContainSingle().Which.Destination.Should().Be("a");
    }

    [Fact]
    public async Task Default_exchange_bindings_are_synthesized_from_the_queue_list()
    {
        Operations.ListQueuesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([Queue("payments-dlq"), Queue("audit.sink")]);

        var result = await new ListExchangeBindingsQueryHandler(Operations, Connections, Log<ListExchangeBindingsQueryHandler>())
            .HandleAsync(ConnectionId, Vhost, "");

        result.Value!.Select(b => (b.Source, b.Destination, b.RoutingKey))
            .Should().BeEquivalentTo([("", "payments-dlq", "payments-dlq"), ("", "audit.sink", "audit.sink")]);
        await Operations.DidNotReceiveWithAnyArgs().ListBindingsAsync(default!, default);
    }

    [Fact]
    public async Task Queues_snapshot_carries_bindings_and_exchanges()
    {
        Operations.ListQueuesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([Queue("q")]);
        Operations.ListBindingsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([Binding("x", "q")]);
        Operations.ListExchangesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new ListQueuesQueryHandler(Operations, Connections, Log<ListQueuesQueryHandler>()).HandleAsync(ConnectionId, Vhost);

        result.Value!.Queues.Should().ContainSingle();
        result.Value.Bindings.Should().ContainSingle();
    }

    [Fact]
    public async Task Queue_detail_snapshot_includes_the_detail()
    {
        var detail = new QueueDetails(Queue("q"), [], []);
        Operations.GetQueueAsync(Secret, Vhost, "q", Arg.Any<CancellationToken>()).Returns(detail);
        Operations.ListQueuesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListBindingsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListExchangesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new GetQueueDetailQueryHandler(Operations, Connections, Log<GetQueueDetailQueryHandler>()).HandleAsync(ConnectionId, Vhost, "q");

        result.Value!.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task Shovel_status_404_means_the_plugin_is_missing_not_an_error()
    {
        Operations.ListShovelsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).ThrowsAsync(new ManagementApiException(404, "GET", "/api/shovels/%2Forders", "Not Found"));
        Operations.ListQueuesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([Queue("q")]);

        var result = await new ListShovelsQueryHandler(Operations, Connections, Log<ListShovelsQueryHandler>()).HandleAsync(ConnectionId, Vhost);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ShovelPluginMissing.Should().BeTrue();
        result.Value.Shovels.Should().BeEmpty();
        result.Value.Queues.Should().ContainSingle();
    }

    [Fact]
    public async Task Any_other_shovel_failure_fails()
    {
        Operations.ListShovelsAsync(Secret, Vhost, Arg.Any<CancellationToken>()).ThrowsAsync(Forbidden);
        Operations.ListQueuesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new ListShovelsQueryHandler(Operations, Connections, Log<ListShovelsQueryHandler>()).HandleAsync(ConnectionId, Vhost);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Policies_snapshot_loads_policies_queues_and_exchanges()
    {
        var policy = new PolicyInfo("dlq-ttl", "-dlq$", "queues", 5, new Dictionary<string, object?>());
        Operations.ListPoliciesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([policy]);
        Operations.ListQueuesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);
        Operations.ListExchangesAsync(Secret, Vhost, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new ListPoliciesQueryHandler(Operations, Connections, Log<ListPoliciesQueryHandler>()).HandleAsync(ConnectionId, Vhost);

        result.Value!.Policies.Should().ContainSingle().Which.Should().Be(policy);
    }

    [Fact]
    public async Task Peek_uses_peek_mode_and_writes_no_audit_row()
    {
        Operations.GetMessagesAsync(Secret, Vhost, "payments-dlq", 25, GetMode.Peek, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new PeekMessagesQueryHandler(Operations, Connections, Log<PeekMessagesQueryHandler>()).HandleAsync(ConnectionId, Vhost, "payments-dlq", 25);

        result.IsSuccess.Should().BeTrue();
        await Operations.Received(1).GetMessagesAsync(Secret, Vhost, "payments-dlq", 25, GetMode.Peek, Arg.Any<CancellationToken>());
        Audit.ReceivedCalls().Should().BeEmpty();
    }
}
