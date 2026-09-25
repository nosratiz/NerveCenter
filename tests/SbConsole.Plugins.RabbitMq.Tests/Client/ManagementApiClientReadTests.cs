using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class ManagementApiClientReadTests
{
    [Fact]
    public async Task ListVhosts_returns_names()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/vhosts", "vhosts.json");

        (await client.ListVhostsAsync(TestSettings.Orders)).Should().Equal("/", "/orders");
    }

    [Fact]
    public async Task Overview_maps_totals_rates_and_churn()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/overview", "overview.json");

        var overview = await client.GetOverviewAsync(TestSettings.Orders);

        overview.Should().BeEquivalentTo(new BrokerOverview(
            ClusterName: "rabbit@local",
            RabbitVersion: "3.13.7",
            ErlangVersion: "26.2.5.16",
            PublishRate: 0.0,
            DeliverRate: null, // no deliver_get_details on this broker yet
            AckRate: null,
            UnroutableRate: 0.0,
            Connections: 2,
            Channels: 2,
            Queues: 10,
            Exchanges: 22,
            Consumers: 1,
            MessagesReady: 26,
            MessagesUnacked: 0,
            ConnectionsOpened: 22,
            ConnectionsClosed: 24,
            ChannelsOpened: 23));
    }

    [Fact]
    public async Task Overview_sums_unroutable_rates_and_reads_deliver_get()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("GET", "/api/overview", """
            {"cluster_name":"c","rabbitmq_version":"4.0.1",
             "message_stats":{"deliver_get_details":{"rate":3.5},"ack_details":{"rate":2.0},
                              "drop_unroutable_details":{"rate":1.5},"return_unroutable_details":{"rate":0.25}}}
            """);

        var overview = await client.GetOverviewAsync(TestSettings.Orders);

        overview.DeliverRate.Should().Be(3.5);
        overview.AckRate.Should().Be(2.0);
        overview.UnroutableRate.Should().Be(1.75);
        overview.PublishRate.Should().BeNull();
        overview.ErlangVersion.Should().BeNull();
        overview.Connections.Should().Be(0);
        overview.ConnectionsOpened.Should().Be(0);
    }

    [Fact]
    public async Task Nodes_map_resources_alarms_and_uptime()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/nodes", "nodes.json");

        var node = (await client.ListNodesAsync(TestSettings.Orders)).Should().ContainSingle().Subject;

        node.Should().Be(new NodeSummary(
            "rabbit@rabbit-local", Running: true,
            MemUsed: 192995328, MemLimit: 3285834137, MemAlarm: false,
            DiskFree: 26561277952, DiskFreeLimit: 50000000, DiskAlarm: false,
            FdUsed: 54, FdTotal: 1048576,
            Uptime: TimeSpan.FromMilliseconds(16840)));
    }

    [Fact]
    public async Task Stopped_node_without_stats_maps_to_zero_and_null_uptime()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("GET", "/api/nodes", """[{"name":"rabbit@down","running":false}]""");

        var node = (await client.ListNodesAsync(TestSettings.Orders)).Single();

        node.Running.Should().BeFalse();
        node.MemUsed.Should().Be(0);
        node.FdTotal.Should().Be(0);
        node.Uptime.Should().BeNull();
    }

    [Fact]
    public async Task Exchanges_map_default_exchange_rates_and_missing_stats()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/exchanges/%2Forders", "exchanges__2Forders.json");

        var exchanges = await client.ListExchangesAsync(TestSettings.Orders, "/orders");

        exchanges.Should().HaveCount(15);
        var @default = exchanges[0];
        @default.Name.Should().BeEmpty();
        @default.IsDefault.Should().BeTrue();
        @default.PublishInRate.Should().Be(0.0);

        var trace = exchanges.Single(e => e.Name == "amq.rabbitmq.trace");
        trace.Internal.Should().BeTrue();
        trace.Type.Should().Be("topic");
        trace.PublishInRate.Should().BeNull();
        trace.PublishOutRate.Should().BeNull();

        exchanges.Single(e => e.Name == "order-events").Should().BeEquivalentTo(new
        {
            Type = "topic",
            Durable = true,
            AutoDelete = false,
            AlternateExchange = (string?)null,
        });
    }

    [Fact]
    public async Task Exchange_alternate_exchange_and_arguments_become_clr_values()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("GET", "/api/exchanges/%2Forders", """
            [{"name":"orders.in","type":"direct","durable":true,"auto_delete":false,"internal":false,
              "arguments":{"alternate-exchange":"orders.unrouted","x-n":5,"x-d":1.5,"x-b":true,"x-null":null,
                           "x-list":["a",2],"x-obj":{"k":"v"}}}]
            """);

        var exchange = (await client.ListExchangesAsync(TestSettings.Orders, "/orders")).Single();

        exchange.AlternateExchange.Should().Be("orders.unrouted");
        exchange.Arguments["x-n"].Should().BeOfType<long>().Which.Should().Be(5L);
        exchange.Arguments["x-d"].Should().BeOfType<double>().Which.Should().Be(1.5);
        exchange.Arguments["x-b"].Should().Be(true);
        exchange.Arguments["x-null"].Should().BeNull();
        exchange.Arguments["x-list"].Should().BeEquivalentTo(new List<object?> { "a", 2L });
        exchange.Arguments["x-obj"].Should().BeEquivalentTo(new Dictionary<string, object?> { ["k"] = "v" });
    }

    [Fact]
    public async Task Queue_dead_letter_comes_from_argument_before_policy()
    {
        var queues = await ListOrdersQueues();

        var q = queues.Single(x => x.Name == "order-events.q");
        q.Vhost.Should().Be("/orders");
        q.Type.Should().Be("classic");
        q.DeadLetterExchange.Should().Be("billing.retry.dlx");
        q.DeadLetterRoutingKey.Should().Be("dlq.order-events");
        q.MaxLength.Should().Be(500000);
        q.Overflow.Should().Be("reject-publish");
        q.Policy.Should().Be("orders-limits");
        q.Ready.Should().Be(7);
        q.Unacked.Should().Be(0);
        q.PublishRate.Should().Be(0.0);
        q.AckRate.Should().BeNull();
        q.RedeliverRate.Should().BeNull();
        q.ReplicaCount.Should().BeNull();
        q.EffectivePolicyDefinition.Should().ContainKey("max-length").WhoseValue.Should().BeOfType<long>().Which.Should().Be(500000L);
        q.Arguments["x-max-length"].Should().BeOfType<long>();
    }

    [Fact]
    public async Task Queue_dead_letter_falls_back_to_effective_policy()
    {
        var q = (await ListOrdersQueues()).Single(x => x.Name == "billing.retry");

        q.DeadLetterExchange.Should().Be("billing.direct");
        q.DeadLetterRoutingKey.Should().BeNull();
        q.MessageTtlMs.Should().Be(30000);
        q.Policy.Should().Be("retry-shortttl");
    }

    [Fact]
    public async Task Queue_ttl_from_policy_only_is_resolved()
    {
        var q = (await ListOrdersQueues()).Single(x => x.Name == "payments-dlq");

        q.MessageTtlMs.Should().Be(604800000);
        q.DeadLetterExchange.Should().BeNull();
    }

    [Fact]
    public async Task Quorum_queue_reports_replica_count()
    {
        var q = (await ListOrdersQueues()).Single(x => x.Name == "audit.sink");

        q.Type.Should().Be("quorum");
        q.ReplicaCount.Should().Be(1);
        q.MemoryBytes.Should().Be(34588);
        q.Durable.Should().BeTrue();
    }

    [Fact]
    public async Task Lazy_queue_mode_is_detected()
    {
        var queues = await ListOrdersQueues();

        queues.Single(x => x.Name == "notify.sms.q").Lazy.Should().BeTrue();
        queues.Single(x => x.Name == "notify.email.q").Lazy.Should().BeFalse();
    }

    [Fact]
    public async Task Queue_without_message_stats_has_null_rates()
    {
        var q = (await ListOrdersQueues()).Single(x => x.Name == "legacy.import.q");

        q.PublishRate.Should().BeNull();
        q.AckRate.Should().BeNull();
        q.RedeliverRate.Should().BeNull();
        q.Consumers.Should().Be(1);
    }

    [Fact]
    public async Task Queue_with_only_a_name_maps_counts_to_zero()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("GET", "/api/queues/%2Forders", """[{"name":"bare","vhost":"/orders"}]""");

        var q = (await client.ListQueuesAsync(TestSettings.Orders, "/orders")).Single();

        q.Type.Should().Be("classic");
        q.Ready.Should().Be(0);
        q.Unacked.Should().Be(0);
        q.Consumers.Should().Be(0);
        q.MemoryBytes.Should().Be(0);
        q.PublishRate.Should().BeNull();
        q.IdleSince.Should().BeNull();
        q.Arguments.Should().BeEmpty();
        q.EffectivePolicyDefinition.Should().BeEmpty();
    }

    [Fact]
    public async Task Queue_rates_idle_since_head_timestamp_and_policy_queue_mode()
    {
        var (client, handler) = TestSettings.Create();
        handler.Respond("GET", "/api/queues/%2Forders", """
            [{"name":"busy","vhost":"/orders","type":"classic","idle_since":"2026-09-25 20:11:56",
              "head_message_timestamp":1790367116,"effective_policy_definition":{"queue-mode":"lazy"},
              "message_stats":{"publish_details":{"rate":4.2},"ack_details":{"rate":3.1},"redeliver_details":{"rate":0.5}}}]
            """);

        var q = (await client.ListQueuesAsync(TestSettings.Orders, "/orders")).Single();

        q.PublishRate.Should().Be(4.2);
        q.AckRate.Should().Be(3.1);
        q.RedeliverRate.Should().Be(0.5);
        q.IdleSince.Should().Be(new DateTimeOffset(2026, 9, 25, 20, 11, 56, TimeSpan.Zero));
        q.HeadMessageTimestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790367116));
        q.Lazy.Should().BeTrue();
    }

    [Fact]
    public async Task ListQueues_without_vhost_reads_every_vhost()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/queues", "queues__2Forders.json");

        var queues = await client.ListQueuesAsync(TestSettings.Orders, null);

        queues.Should().HaveCount(10);
        handler.Requests.Single().Uri.AbsolutePath.Should().Be("/api/queues");
    }

    [Fact]
    public async Task GetQueue_maps_consumers_bindings_and_iso_idle_since()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/queues/%2Forders/legacy.import.q", "queues__2Forders_legacy.import.q.json")
            .Respond("GET", "/api/queues/%2Forders/legacy.import.q/bindings",
                """[{"source":"","vhost":"/orders","destination":"legacy.import.q","destination_type":"queue","routing_key":"legacy.import.q","arguments":{},"properties_key":"legacy.import.q"}]""");

        var details = await client.GetQueueAsync(TestSettings.Orders, "/orders", "legacy.import.q");

        details.Summary.Name.Should().Be("legacy.import.q");
        details.Summary.IdleSince.Should().Be(new DateTimeOffset(2026, 9, 25, 20, 11, 57, 81, TimeSpan.Zero));
        details.Summary.HeadMessageTimestamp.Should().BeNull();
        var consumer = details.Consumers.Should().ContainSingle().Subject;
        consumer.Should().Be(new ConsumerInfo(
            "amq.ctag-43ZK8l9NYY0wNptW9yp7uA",
            "<rabbit@rabbit-local.1790367115.920.0> (1)",
            Prefetch: 1000, AckRequired: true, Exclusive: false));
        details.Bindings.Should().ContainSingle().Which.Source.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQueue_bindings_include_the_default_exchange_binding()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/queues/%2Forders/order-events.q", "queues__2Forders_order-events.q.json")
            .RespondFixture("/api/queues/%2Forders/order-events.q/bindings", "queues__2Forders_order-events.q_bindings.json");

        var details = await client.GetQueueAsync(TestSettings.Orders, "/orders", "order-events.q");

        details.Consumers.Should().BeEmpty();
        details.Bindings.Select(b => b.Source).Should().Equal("", "audit.fanout", "order-events", "order-events");
        details.Bindings[2].Should().BeEquivalentTo(new
        {
            Destination = "order-events.q",
            DestinationType = "queue",
            RoutingKey = "order.*.amended",
            PropertiesKey = "order.%2A.amended",
            IsToQueue = true,
        });
        details.Summary.DeadLetterExchange.Should().Be("billing.retry.dlx");
    }

    [Fact]
    public async Task Bindings_for_a_vhost_and_for_all_vhosts()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/bindings/%2Forders", "bindings__2Forders.json")
            .RespondFixture("/api/bindings", "bindings__2Forders.json");

        var inVhost = await client.ListBindingsAsync(TestSettings.Orders, "/orders");
        var all = await client.ListBindingsAsync(TestSettings.Orders, null);

        inVhost.Should().NotBeEmpty();
        all.Should().HaveCount(inVhost.Count);
        inVhost.Should().Contain(b => b.Source == "order-events" && b.Destination == "audit.sink" && b.RoutingKey == "#" && b.PropertiesKey == "%23");
        handler.Requests.Select(r => r.Uri.AbsolutePath).Should().Equal("/api/bindings/%2Forders", "/api/bindings");
    }

    [Fact]
    public async Task Policies_map_apply_to_priority_and_definition()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/policies/%2Forders", "policies__2Forders.json");

        var policies = await client.ListPoliciesAsync(TestSettings.Orders, "/orders");

        policies.Should().HaveCount(3);
        var retry = policies.Single(p => p.Name == "retry-shortttl");
        retry.Pattern.Should().Be("\\.retry$");
        retry.ApplyTo.Should().Be("queues");
        retry.Priority.Should().Be(5);
        retry.Definition.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["dead-letter-exchange"] = "billing.direct",
            ["message-ttl"] = 30000L,
        });
        retry.Definition["message-ttl"].Should().BeOfType<long>();
    }

    private static async Task<IReadOnlyList<QueueSummary>> ListOrdersQueues()
    {
        var (client, handler) = TestSettings.Create();
        handler.RespondFixture("/api/queues/%2Forders", "queues__2Forders.json");
        return await client.ListQueuesAsync(TestSettings.Orders, "/orders");
    }
}
