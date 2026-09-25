using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;
using static SbConsole.Plugins.RabbitMq.Tests.Routing.RoutingFixtures;

namespace SbConsole.Plugins.RabbitMq.Tests.Routing;

public class DeadLetterTopologyTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] a) => a.ToDictionary(x => x.Key, x => x.Value);

    // The docker seed topology (docker/rabbitmq).
    private static readonly QueueSummary[] SeededQueues =
    [
        Queue("order-events.q", "billing.retry.dlx", "dlq.order-events",
            Args(("x-dead-letter-exchange", "billing.retry.dlx"), ("x-dead-letter-routing-key", "dlq.order-events")), policy: "orders-limits"),
        Queue("billing.payments.q", "billing.retry.dlx", "dlq.payments",
            Args(("x-dead-letter-exchange", "billing.retry.dlx"), ("x-dead-letter-routing-key", "dlq.payments"))),
        Queue("billing.retry", "billing.direct", null, policy: "retry-shortttl"),
        Queue("payments-dlq", policy: "dlq-ttl"),
        Queue("audit.sink"),
    ];

    private static readonly BindingInfo[] SeededBindings =
    [
        Binding("billing.retry.dlx", "payments-dlq", "dlq.order-events"),
        Binding("billing.retry.dlx", "payments-dlq", "dlq.payments"),
        Binding("billing.direct", "billing.payments.q", "payment.capture"),
        Binding("billing.direct", "billing.payments.q", "refund.issue"),
        Binding("order-events", "order-events.q", "order.*.created"),
        Binding("order-events", "audit.sink", "#"),
    ];

    private static readonly ExchangeSummary[] SeededExchanges =
    [
        Exchange("", "direct"),
        Exchange("order-events", "topic"),
        Exchange("billing.retry.dlx", "direct"),
        Exchange("billing.direct", "direct"),
    ];

    private static DeadLetterTopology Seeded() => DeadLetterTopology.Compute(SeededQueues, SeededBindings, SeededExchanges);

    [Fact]
    public void Seeded_topology_dead_letter_exchanges()
    {
        Seeded().DeadLetterExchanges.Should().BeEquivalentTo("billing.retry.dlx", "billing.direct");
    }

    [Fact]
    public void Seeded_topology_only_payments_dlq_is_a_DLQ()
    {
        var topology = Seeded();

        topology.DeadLetterQueues.Should().BeEquivalentTo("payments-dlq");
        topology.IsDeadLetterQueue("payments-dlq").Should().BeTrue();
        // billing.payments.q is bound from billing.direct (billing.retry's DLX) but has its own DLX:
        // it's the work queue a retry loop returns to, not a DLQ.
        topology.IsDeadLetterQueue("billing.payments.q").Should().BeFalse();
        topology.IsDeadLetterQueue("order-events.q").Should().BeFalse();
        topology.IsDeadLetterQueue("nope").Should().BeFalse();
    }

    [Fact]
    public void RouteOut_with_argument_DLRK()
    {
        var route = Seeded().RouteOut("order-events.q");

        route.Should().NotBeNull();
        route!.Exchange.Should().Be("billing.retry.dlx");
        route.RoutingKey.Should().Be("dlq.order-events");
        route.TargetQueues.Should().Equal("payments-dlq");
        route.DependsOnMessageRoutingKey.Should().BeFalse();
        route.SetBy.Should().Be("argument");
    }

    [Fact]
    public void RouteOut_policy_DLX_without_DLRK_lists_every_bound_queue()
    {
        var route = Seeded().RouteOut("billing.retry");

        route.Should().NotBeNull();
        route!.Exchange.Should().Be("billing.direct");
        route.RoutingKey.Should().BeNull();
        route.TargetQueues.Should().Equal("billing.payments.q");
        route.DependsOnMessageRoutingKey.Should().BeTrue();
        route.SetBy.Should().Be("retry-shortttl");
    }

    [Fact]
    public void RouteOut_is_null_for_queue_without_DLX_or_unknown_queue()
    {
        Seeded().RouteOut("payments-dlq").Should().BeNull();
        Seeded().RouteOut("nope").Should().BeNull();
    }

    [Fact]
    public void SetBy_falls_back_to_policy_when_policy_name_missing()
    {
        var topology = DeadLetterTopology.Compute([Queue("q", "dlx", null)], [], []);
        topology.RouteOut("q")!.SetBy.Should().Be("policy");
    }

    [Fact]
    public void Default_exchange_DLX_with_DLRK_makes_the_named_queue_a_DLQ()
    {
        var queues = new[]
        {
            Queue("work", "", "work.dead", Args(("x-dead-letter-exchange", ""), ("x-dead-letter-routing-key", "work.dead"))),
            Queue("work.dead"),
        };

        var topology = DeadLetterTopology.Compute(queues, [], [Exchange("", "direct")]);

        topology.DeadLetterQueues.Should().BeEquivalentTo("work.dead");
        topology.DeadLetterExchanges.Should().BeEmpty();
        var route = topology.RouteOut("work")!;
        route.Exchange.Should().Be("");
        route.TargetQueues.Should().Equal("work.dead");
        route.DependsOnMessageRoutingKey.Should().BeFalse();
    }

    [Fact]
    public void Default_exchange_DLRK_naming_a_queue_with_its_own_DLX_is_not_a_DLQ()
    {
        var queues = new[]
        {
            Queue("work", "", "retry"),
            Queue("retry", "", "work"),
        };

        DeadLetterTopology.Compute(queues, [], []).DeadLetterQueues.Should().BeEmpty();
    }

    [Fact]
    public void Default_exchange_DLX_without_DLRK_depends_on_message_key()
    {
        var topology = DeadLetterTopology.Compute([Queue("work", "", null), Queue("other")], [], []);

        var route = topology.RouteOut("work")!;
        route.TargetQueues.Should().BeEmpty();
        route.DependsOnMessageRoutingKey.Should().BeTrue();
        topology.DeadLetterQueues.Should().BeEmpty();
    }

    [Fact]
    public void Fanout_DLX_routes_to_every_bound_queue_independent_of_key()
    {
        var queues = new[] { Queue("work", "dlx.fan", "ignored"), Queue("dead1"), Queue("dead2") };
        var bindings = new[] { Binding("dlx.fan", "dead1", ""), Binding("dlx.fan", "dead2", "x"), Binding("dlx.fan", "ex2", "", destinationType: "exchange") };

        var route = DeadLetterTopology.Compute(queues, bindings, [Exchange("dlx.fan", "fanout")]).RouteOut("work")!;

        route.TargetQueues.Should().Equal("dead1", "dead2");
        route.DependsOnMessageRoutingKey.Should().BeFalse();
    }

    [Fact]
    public void Topic_DLX_resolves_DLRK_through_the_matcher()
    {
        var queues = new[] { Queue("work", "dlx.topic", "dead.orders.eu"), Queue("eu"), Queue("all"), Queue("us") };
        var bindings = new[]
        {
            Binding("dlx.topic", "eu", "dead.*.eu"),
            Binding("dlx.topic", "all", "dead.#"),
            Binding("dlx.topic", "us", "dead.*.us"),
        };

        var route = DeadLetterTopology.Compute(queues, bindings, [Exchange("dlx.topic", "topic")]).RouteOut("work")!;

        route.TargetQueues.Should().Equal("eu", "all");
        route.DependsOnMessageRoutingKey.Should().BeFalse();
    }

    [Fact]
    public void Unlisted_DLX_is_treated_as_direct()
    {
        var queues = new[] { Queue("work", "missing.dlx", "k"), Queue("dead"), Queue("other") };
        var bindings = new[] { Binding("missing.dlx", "dead", "k"), Binding("missing.dlx", "other", "k.#") };

        DeadLetterTopology.Compute(queues, bindings, []).RouteOut("work")!.TargetQueues.Should().Equal("dead");
    }

    [Fact]
    public void ComputePerVhost_keeps_vhosts_apart()
    {
        var queues = new[]
        {
            Queue("work", "dlx", "k", vhost: "a"),
            Queue("dead", vhost: "a"),
            Queue("dead", vhost: "b"),
        };
        var bindings = new Dictionary<string, IReadOnlyList<BindingInfo>>
        {
            ["a"] = [Binding("dlx", "dead", "k")],
            ["b"] = [Binding("dlx", "dead", "k")],
        };

        var perVhost = DeadLetterTopology.ComputePerVhost(queues, bindings, new Dictionary<string, IReadOnlyList<ExchangeSummary>>());

        perVhost.Keys.Should().BeEquivalentTo("a", "b");
        perVhost["a"].DeadLetterQueues.Should().BeEquivalentTo("dead");
        perVhost["b"].DeadLetterQueues.Should().BeEmpty();
    }

    [Fact]
    public void ComputePerVhost_tolerates_missing_bindings_for_a_vhost()
    {
        var perVhost = DeadLetterTopology.ComputePerVhost(
            [Queue("work", "dlx", null, vhost: "a")],
            new Dictionary<string, IReadOnlyList<BindingInfo>>(),
            new Dictionary<string, IReadOnlyList<ExchangeSummary>>());

        perVhost["a"].RouteOut("work")!.TargetQueues.Should().BeEmpty();
    }
}
