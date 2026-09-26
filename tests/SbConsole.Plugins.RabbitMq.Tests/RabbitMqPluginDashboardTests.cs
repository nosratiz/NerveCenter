using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;
using SbConsole.Sdk;
using static SbConsole.Plugins.RabbitMq.Tests.Routing.RoutingFixtures;

namespace SbConsole.Plugins.RabbitMq.Tests;

// Covers the badge/dashboard/resource-metric hooks through RabbitMqPlugin's internal static helpers
// over a BrokerSnapshot -- the public IPlugin entry points build a real RabbitOperations, which needs
// a broker, so (same as AwsPluginDashboardTests) only the snapshot fetch over a substituted
// IRabbitOperations, the pure derivations and the cache's reuse/expiry behaviour are exercised.
public class RabbitMqPluginDashboardTests
{
    private static readonly Guid ConnectionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static QueueSummary Q(string name, long ready = 0, int consumers = 0, string vhost = "/", string? dlx = null) =>
        Queue(name, dlx: dlx, vhost: vhost) with { Ready = ready, Consumers = consumers };

    private static NodeSummary Node(string name, bool memAlarm = false, bool diskAlarm = false) =>
        new(name, Running: true, MemUsed: 1, MemLimit: 2, memAlarm, DiskFree: 10, DiskFreeLimit: 1, diskAlarm,
            FdUsed: 1, FdTotal: 10, Uptime: null);

    // "/" : orders (dlx "dlx") -> dlx bound to orders.dlq (a DLQ). "/" also has idle (no consumers).
    // "tenant a" : jobs (dlx "dlx") -> dlx bound to jobs.dlq.
    private static RabbitMqPlugin.BrokerSnapshot Snapshot(
        IReadOnlyList<NodeSummary>? nodes = null,
        long ordersReady = 0, int ordersConsumers = 1,
        long ordersDlqReady = 0,
        long jobsDlqReady = 0,
        long idleReady = 0, int idleConsumers = 0)
    {
        var queues = new List<QueueSummary>
        {
            Q("orders", ordersReady, ordersConsumers, dlx: "dlx"),
            Q("orders.dlq", ordersDlqReady),
            Q("idle", idleReady, idleConsumers),
            Q("jobs", 0, 1, vhost: "tenant a", dlx: "dlx"),
            Q("jobs.dlq", jobsDlqReady, vhost: "tenant a"),
        };
        var topology = DeadLetterTopology.ComputePerVhost(
            queues,
            new Dictionary<string, IReadOnlyList<BindingInfo>>
            {
                ["/"] = [Binding("dlx", "orders.dlq", "orders")],
                ["tenant a"] = [Binding("dlx", "jobs.dlq", "jobs")],
            },
            new Dictionary<string, IReadOnlyList<ExchangeSummary>>
            {
                ["/"] = [Exchange("dlx", "direct")],
                ["tenant a"] = [Exchange("dlx", "direct")],
            });
        return new RabbitMqPlugin.BrokerSnapshot(nodes ?? [Node("rabbit@a")], queues, topology);
    }

    // --- snapshot fetch ---

    [Fact]
    public async Task FetchSnapshotAsync_fetches_bindings_and_exchanges_per_vhost_and_computes_topology()
    {
        var ops = Substitute.For<IRabbitOperations>();
        ops.ListNodesAsync("s", Arg.Any<CancellationToken>()).Returns([Node("rabbit@a", memAlarm: true)]);
        ops.ListQueuesAsync("s", null, Arg.Any<CancellationToken>()).Returns(
        [
            Q("orders", dlx: "dlx"), Q("orders.dlq", 4),
            Q("jobs", vhost: "tenant a", dlx: "dlx"), Q("jobs.dlq", 2, vhost: "tenant a"),
        ]);
        ops.ListBindingsAsync("s", "/", Arg.Any<CancellationToken>()).Returns([Binding("dlx", "orders.dlq", "orders")]);
        ops.ListBindingsAsync("s", "tenant a", Arg.Any<CancellationToken>()).Returns([Binding("dlx", "jobs.dlq", "jobs")]);
        ops.ListExchangesAsync("s", "/", Arg.Any<CancellationToken>()).Returns([Exchange("dlx", "direct")]);
        ops.ListExchangesAsync("s", "tenant a", Arg.Any<CancellationToken>()).Returns([Exchange("dlx", "direct")]);

        var snapshot = await RabbitMqPlugin.FetchSnapshotAsync(ops, "s", CancellationToken.None);

        snapshot.Nodes.Should().ContainSingle().Which.Name.Should().Be("rabbit@a");
        snapshot.Queues.Should().HaveCount(4);
        snapshot.TopologyByVhost.Keys.Should().BeEquivalentTo("/", "tenant a");
        snapshot.TopologyByVhost["/"].DeadLetterQueues.Should().BeEquivalentTo("orders.dlq");
        snapshot.TopologyByVhost["tenant a"].DeadLetterQueues.Should().BeEquivalentTo("jobs.dlq");
        await ops.Received(1).ListBindingsAsync("s", "/", Arg.Any<CancellationToken>());
        await ops.Received(1).ListBindingsAsync("s", "tenant a", Arg.Any<CancellationToken>());
        await ops.DidNotReceive().ListBindingsAsync("s", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchSnapshotAsync_with_no_queues_makes_no_per_vhost_calls()
    {
        var ops = Substitute.For<IRabbitOperations>();
        ops.ListNodesAsync("s", Arg.Any<CancellationToken>()).Returns([Node("rabbit@a")]);
        ops.ListQueuesAsync("s", null, Arg.Any<CancellationToken>()).Returns([]);

        var snapshot = await RabbitMqPlugin.FetchSnapshotAsync(ops, "s", CancellationToken.None);

        snapshot.Queues.Should().BeEmpty();
        snapshot.TopologyByVhost.Should().BeEmpty();
        await ops.DidNotReceiveWithAnyArgs().ListBindingsAsync(default!, default, default);
        await ops.DidNotReceiveWithAnyArgs().ListExchangesAsync(default!, default!, default);
    }

    [Fact]
    public async Task FetchSnapshotAsync_propagates_a_failure()
    {
        var ops = Substitute.For<IRabbitOperations>();
        ops.ListNodesAsync("s", Arg.Any<CancellationToken>()).Returns<IReadOnlyList<NodeSummary>>(_ => throw new HttpRequestException("down"));
        ops.ListQueuesAsync("s", null, Arg.Any<CancellationToken>()).Returns([]);

        var act = () => RabbitMqPlugin.FetchSnapshotAsync(ops, "s", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // --- cache ---

    [Fact]
    public async Task GetCachedSnapshotAsync_reuses_the_result_within_the_TTL()
    {
        var secret = $"s-{Guid.NewGuid()}";
        var calls = 0;
        var snapshot = Snapshot();
        Task<RabbitMqPlugin.BrokerSnapshot> Fetch(string s, CancellationToken ct) { calls++; return Task.FromResult(snapshot); }

        var now = DateTimeOffset.UtcNow;
        var first = await RabbitMqPlugin.GetCachedSnapshotAsync(secret, now, Fetch, CancellationToken.None);
        var second = await RabbitMqPlugin.GetCachedSnapshotAsync(secret, now.AddSeconds(59), Fetch, CancellationToken.None);

        calls.Should().Be(1);
        first.Should().BeSameAs(snapshot);
        second.Should().BeSameAs(snapshot);
    }

    [Fact]
    public async Task GetCachedSnapshotAsync_fetches_again_after_the_TTL()
    {
        var secret = $"s-{Guid.NewGuid()}";
        var calls = 0;
        Task<RabbitMqPlugin.BrokerSnapshot> Fetch(string s, CancellationToken ct) { calls++; return Task.FromResult(Snapshot()); }

        var now = DateTimeOffset.UtcNow;
        var first = await RabbitMqPlugin.GetCachedSnapshotAsync(secret, now, Fetch, CancellationToken.None);
        var second = await RabbitMqPlugin.GetCachedSnapshotAsync(secret, now.AddSeconds(61), Fetch, CancellationToken.None);

        calls.Should().Be(2);
        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public async Task GetCachedSnapshotAsync_never_caches_a_failure()
    {
        var secret = $"s-{Guid.NewGuid()}";
        var calls = 0;
        var snapshot = Snapshot();
        Task<RabbitMqPlugin.BrokerSnapshot> Fetch(string s, CancellationToken ct)
        {
            calls++;
            return calls == 1 ? throw new HttpRequestException("down") : Task.FromResult(snapshot);
        }

        var now = DateTimeOffset.UtcNow;
        var act = () => RabbitMqPlugin.GetCachedSnapshotAsync(secret, now, Fetch, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();

        var retried = await RabbitMqPlugin.GetCachedSnapshotAsync(secret, now.AddSeconds(1), Fetch, CancellationToken.None);

        calls.Should().Be(2);
        retried.Should().BeSameAs(snapshot);
    }

    // --- nav badges ---

    [Fact]
    public void Overview_badge_counts_nodes_in_memory_or_disk_alarm()
    {
        var snapshot = Snapshot(nodes: [Node("a", memAlarm: true), Node("b", diskAlarm: true), Node("c", memAlarm: true, diskAlarm: true), Node("d")]);

        RabbitMqPlugin.ComputeNavBadge("/p/rabbitmq/overview", snapshot).Should().Be(3);
    }

    [Fact]
    public void Overview_badge_is_null_not_zero_when_no_node_is_in_alarm()
    {
        RabbitMqPlugin.ComputeNavBadge("/p/rabbitmq/overview", Snapshot(nodes: [Node("a")])).Should().BeNull();
    }

    [Fact]
    public void Queues_badge_sums_ready_over_dead_letter_queues_across_vhosts()
    {
        var snapshot = Snapshot(ordersReady: 100, ordersDlqReady: 4, jobsDlqReady: 2, idleReady: 50);

        RabbitMqPlugin.ComputeNavBadge("/p/rabbitmq/queues", snapshot).Should().Be(6);
    }

    [Fact]
    public void Queues_badge_is_null_not_zero_when_nothing_is_dead_lettered()
    {
        RabbitMqPlugin.ComputeNavBadge("/p/rabbitmq/queues", Snapshot(ordersReady: 100)).Should().BeNull();
    }

    [Fact]
    public void Queues_badge_clamps_to_int_max()
    {
        var snapshot = Snapshot(ordersDlqReady: long.MaxValue / 2, jobsDlqReady: long.MaxValue / 2);

        RabbitMqPlugin.ComputeNavBadge("/p/rabbitmq/queues", snapshot).Should().Be(int.MaxValue);
    }

    [Theory]
    [InlineData("/p/rabbitmq/exchanges")]
    [InlineData("/p/rabbitmq/shovels")]
    public void Other_nav_items_have_no_badge(string href)
    {
        var snapshot = Snapshot(nodes: [Node("a", memAlarm: true)], ordersDlqReady: 4);

        RabbitMqPlugin.ComputeNavBadge(href, snapshot).Should().BeNull();
    }

    // --- dashboard metrics ---

    [Fact]
    public void Dashboard_metrics_are_queue_count_and_dead_lettered_total()
    {
        var metrics = RabbitMqPlugin.BuildDashboardMetrics(Snapshot(ordersReady: 9, ordersDlqReady: 4, jobsDlqReady: 2));

        metrics.Should().Equal(
            new PluginDashboardMetric("Queues", 5),
            new PluginDashboardMetric("Dead-lettered", 6));
    }

    // --- resource metrics ---

    [Fact]
    public void Resource_metrics_are_one_per_queue_named_vhost_slash_name()
    {
        var metrics = RabbitMqPlugin.BuildResourceMetrics(Snapshot(ordersReady: 9, ordersDlqReady: 4, jobsDlqReady: 2, idleReady: 1));

        metrics.Should().BeEquivalentTo(new[]
        {
            new PluginResourceMetric("//orders", 9, 0),
            new PluginResourceMetric("//orders.dlq", 4, 4),
            new PluginResourceMetric("//idle", 1, 0),
            new PluginResourceMetric("tenant a/jobs", 0, 0),
            new PluginResourceMetric("tenant a/jobs.dlq", 2, 2),
        });
    }

    // --- problems ---

    [Fact]
    public void Node_alarms_are_errors_linking_to_the_overview()
    {
        var problems = RabbitMqPlugin.BuildDashboardProblems(ConnectionId,
            Snapshot(nodes: [Node("rabbit@a", memAlarm: true, diskAlarm: true), Node("rabbit@b")]));

        problems.Should().Equal(
            new PluginDashboardProblem("Error", "rabbit@a", "Memory alarm — publishers blocked", $"/p/rabbitmq/overview?connectionId={ConnectionId}"),
            new PluginDashboardProblem("Error", "rabbit@a", "Disk alarm — publishers blocked", $"/p/rabbitmq/overview?connectionId={ConnectionId}"));
    }

    [Fact]
    public void A_dead_letter_backlog_is_a_warning_linking_to_queue_detail_with_escaped_vhost()
    {
        var problems = RabbitMqPlugin.BuildDashboardProblems(ConnectionId, Snapshot(ordersDlqReady: 1234, jobsDlqReady: 2));

        problems.Should().Equal(
            new PluginDashboardProblem("Warning", "orders.dlq", "1,234 dead-lettered",
                $"/p/rabbitmq/queues/orders.dlq?connectionId={ConnectionId}&vhost=%2F"),
            new PluginDashboardProblem("Warning", "tenant a/jobs.dlq", "2 dead-lettered",
                $"/p/rabbitmq/queues/jobs.dlq?connectionId={ConnectionId}&vhost=tenant%20a"));
    }

    [Fact]
    public void A_queue_with_ready_messages_and_no_consumers_is_a_warning()
    {
        var problems = RabbitMqPlugin.BuildDashboardProblems(ConnectionId, Snapshot(idleReady: 7, idleConsumers: 0));

        problems.Should().Equal(
            new PluginDashboardProblem("Warning", "idle", "7 ready, no consumers",
                $"/p/rabbitmq/queues/idle?connectionId={ConnectionId}&vhost=%2F"));
    }

    [Fact]
    public void A_consumed_queue_or_an_empty_one_is_not_a_problem()
    {
        var problems = RabbitMqPlugin.BuildDashboardProblems(ConnectionId,
            Snapshot(ordersReady: 50, ordersConsumers: 3, idleReady: 0, idleConsumers: 0));

        problems.Should().BeEmpty();
    }

    [Fact]
    public void A_dead_letter_queue_is_not_also_reported_as_having_no_consumers()
    {
        var problems = RabbitMqPlugin.BuildDashboardProblems(ConnectionId, Snapshot(ordersDlqReady: 3));

        problems.Should().ContainSingle().Which.Detail.Should().Be("3 dead-lettered");
    }

    [Fact]
    public void A_queue_name_needing_escaping_is_escaped_in_the_link()
    {
        var queues = new List<QueueSummary> { Q("a b/c", ready: 1) };
        var snapshot = new RabbitMqPlugin.BrokerSnapshot([], queues,
            DeadLetterTopology.ComputePerVhost(queues, new Dictionary<string, IReadOnlyList<BindingInfo>>(), new Dictionary<string, IReadOnlyList<ExchangeSummary>>()));

        RabbitMqPlugin.BuildDashboardProblems(ConnectionId, snapshot).Single().LinkHref
            .Should().Be($"/p/rabbitmq/queues/a%20b%2Fc?connectionId={ConnectionId}&vhost=%2F");
    }

    // --- contribution / oldest dead letter ---

    [Fact]
    public void Contribution_counts_six_pages_and_thirteen_actions()
    {
        new RabbitMqPlugin().Contribution.Should().Be(new PluginContribution(PageCount: 6, ActionCount: 13));
    }

    [Fact]
    public async Task Oldest_dead_letter_is_the_sdk_default_null()
    {
        IPlugin plugin = new RabbitMqPlugin();

        (await plugin.GetOldestDeadLetterAsync(ConnectionId, "host=unused")).Should().BeNull();
    }
}
