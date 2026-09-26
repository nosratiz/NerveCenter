using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;
using static SbConsole.Plugins.RabbitMq.Tests.Routing.RoutingFixtures;

namespace SbConsole.Plugins.RabbitMq.Tests.Routing;

public class RoutingMatcherTests
{
    [Theory]
    // Classic tutorial cases.
    [InlineData("*.orange.*", "quick.orange.rabbit", true)]
    [InlineData("*.orange.*", "quick.orange.male.rabbit", false)]
    [InlineData("*.orange.*", "orange", false)]
    [InlineData("*.*.rabbit", "lazy.orange.rabbit", true)]
    [InlineData("*.*.rabbit", "quick.rabbit", false)]
    [InlineData("lazy.#", "lazy", true)]
    [InlineData("lazy.#", "lazy.orange.male.rabbit", true)]
    [InlineData("lazy.#", "lazier.orange", false)]
    [InlineData("#", "anything.at.all", true)]
    [InlineData("#", "x", true)]
    [InlineData("#.#", "a.b.c", true)]
    [InlineData("#.#", "a", true)]
    [InlineData("a.#.b", "a.b", true)]
    [InlineData("a.#.b", "a.x.y.b", true)]
    [InlineData("a.#.b", "a.x.y", false)]
    [InlineData("a.*.#", "a", false)]
    [InlineData("a.*.#", "a.b", true)]
    [InlineData("a.*.#", "a.b.c.d", true)]
    [InlineData("order.*.created", "order.uk.created", true)]
    [InlineData("order.*.created", "order.created", false)]
    [InlineData("exact.key", "exact.key", true)]
    [InlineData("exact.key", "Exact.key", false)]
    // Empty routing key = zero words (RabbitMQ's split_topic_key(<<>>) is []).
    [InlineData("#", "", true)]
    [InlineData("#.#", "", true)]
    [InlineData("*", "", false)]
    [InlineData("", "", true)]
    [InlineData("a", "", false)]
    // An empty word between dots is a real (empty) word.
    [InlineData("a.*.b", "a..b", true)]
    [InlineData("a..b", "a..b", true)]
    [InlineData("a.b", "a..b", false)]
    [InlineData("a.#.b", "a..b", true)]
    [InlineData("*", ".", false)]
    [InlineData("*.*", ".", true)]
    [InlineData("a.*", "a.", true)]
    public void TopicMatches_follows_AMQP_word_semantics(string pattern, string key, bool expected) =>
        RoutingMatcher.TopicMatches(pattern, key).Should().Be(expected);

    private static readonly ExchangeSummary OrderEvents = Exchange("order-events", "topic");

    private static readonly BindingInfo Created = Binding("order-events", "order-events.q", "order.*.created");
    private static readonly BindingInfo Amended = Binding("order-events", "order-events.q", "order.*.amended");
    private static readonly BindingInfo Audit = Binding("order-events", "audit.sink", "#");

    [Fact]
    public void Topic_mockup_case_matches_queue_and_audit_sink()
    {
        var preview = RoutingMatcher.Resolve(OrderEvents, [Created, Amended, Audit], "order.uk.created", NoHeaders);

        preview.Matched.Should().Equal(Created, Audit);
        preview.Closest.Should().BeEmpty();
        preview.HasAlternateExchange.Should().BeFalse();
        preview.IsUnknownType.Should().BeFalse();
    }

    [Fact]
    public void Topic_no_match_offers_closest_bindings()
    {
        var preview = RoutingMatcher.Resolve(OrderEvents, [Created, Amended], "order.created", NoHeaders);

        preview.Matched.Should().BeEmpty();
        preview.Closest.Should().Equal(Created, Amended);
    }

    [Fact]
    public void Closest_is_capped_at_three_and_deduplicated_by_key()
    {
        var bindings = new[]
        {
            Binding("x", "q1", "a.b.c"),
            Binding("x", "q2", "a.b.c"),
            Binding("x", "q3", "a.b.d"),
            Binding("x", "q4", "a.z"),
            Binding("x", "q5", "zzz"),
        };

        var preview = RoutingMatcher.Resolve(Exchange("x", "direct"), bindings, "a.b.e", NoHeaders);

        preview.Closest.Select(b => b.Destination).Should().Equal("q1", "q3", "q4");
    }

    [Fact]
    public void Direct_matches_exact_ordinal_key_only()
    {
        var hit = Binding("billing.direct", "billing.payments.q", "payment.capture");
        var other = Binding("billing.direct", "billing.payments.q", "refund.issue");

        var preview = RoutingMatcher.Resolve(Exchange("billing.direct", "direct"), [hit, other], "payment.capture", NoHeaders);
        preview.Matched.Should().Equal(hit);

        var miss = RoutingMatcher.Resolve(Exchange("billing.direct", "direct"), [hit, other], "Payment.capture", NoHeaders);
        miss.Matched.Should().BeEmpty();
        miss.Closest.Should().HaveCount(2).And.StartWith(hit);
    }

    [Fact]
    public void Fanout_matches_every_binding_regardless_of_key()
    {
        var a = Binding("f", "q1", "");
        var b = Binding("f", "q2", "whatever");

        RoutingMatcher.Resolve(Exchange("f", "fanout"), [a, b], "nope", NoHeaders).Matched.Should().Equal(a, b);
    }

    [Fact]
    public void Fanout_with_no_bindings_offers_no_closest()
    {
        var preview = RoutingMatcher.Resolve(Exchange("f", "fanout"), [], "k", NoHeaders);
        preview.Matched.Should().BeEmpty();
        preview.Closest.Should().BeEmpty();
    }

    [Fact]
    public void Default_exchange_routes_to_queue_named_by_key()
    {
        var bindings = RoutingMatcher.SynthesizeDefaultExchangeBindings(["payments-dlq", "audit.sink"]);

        bindings.Should().HaveCount(2);
        bindings[0].Should().Match<BindingInfo>(b =>
            b.Source == "" && b.Destination == "payments-dlq" && b.RoutingKey == "payments-dlq" && b.DestinationType == "queue");

        var preview = RoutingMatcher.Resolve(Exchange("", "direct"), bindings, "audit.sink", NoHeaders);
        preview.Matched.Select(b => b.Destination).Should().Equal("audit.sink");

        var miss = RoutingMatcher.Resolve(Exchange("", "direct"), bindings, "audit.snk", NoHeaders);
        miss.Matched.Should().BeEmpty();
        miss.Closest.First().Destination.Should().Be("audit.sink");
    }

    [Theory]
    [InlineData("x-consistent-hash")]
    [InlineData("x-delayed-message")]
    [InlineData("x-random")]
    public void Unknown_exchange_types_match_nothing_and_are_flagged(string type)
    {
        var preview = RoutingMatcher.Resolve(Exchange("odd", type), [Binding("odd", "q", "k")], "k", NoHeaders);

        preview.IsUnknownType.Should().BeTrue();
        preview.Matched.Should().BeEmpty();
        preview.Closest.Should().BeEmpty();
    }

    [Fact]
    public void Alternate_exchange_is_flagged()
    {
        RoutingMatcher.Resolve(Exchange("x", "direct", alternateExchange: "unrouted"), [], "k", NoHeaders)
            .HasAlternateExchange.Should().BeTrue();
        RoutingMatcher.Resolve(Exchange("x", "direct", alternateExchange: ""), [], "k", NoHeaders)
            .HasAlternateExchange.Should().BeFalse();
    }

    // ------------------------------------------------------------------ headers

    private static readonly ExchangeSummary Headers = Exchange("h", "headers");

    private static BindingInfo HeadersBinding(string dest, params (string Key, object? Value)[] args) =>
        Binding("h", dest, "", args.ToDictionary(a => a.Key, a => a.Value));

    private static Dictionary<string, string> H(params (string Key, string Value)[] h) => h.ToDictionary(x => x.Key, x => x.Value);

    [Fact]
    public void Headers_all_is_the_default_and_requires_every_argument()
    {
        var b = HeadersBinding("q", ("format", "pdf"), ("type", "report"));

        RoutingMatcher.Resolve(Headers, [b], "", H(("format", "pdf"), ("type", "report"), ("extra", "1"))).Matched.Should().Equal(b);
        RoutingMatcher.Resolve(Headers, [b], "", H(("format", "pdf"))).Matched.Should().BeEmpty();
        RoutingMatcher.Resolve(Headers, [b], "", H(("format", "pdf"), ("type", "log"))).Matched.Should().BeEmpty();
    }

    [Fact]
    public void Headers_any_requires_one_argument()
    {
        var b = HeadersBinding("q", ("x-match", "any"), ("format", "pdf"), ("type", "report"));

        RoutingMatcher.Resolve(Headers, [b], "", H(("type", "report"))).Matched.Should().Equal(b);
        RoutingMatcher.Resolve(Headers, [b], "", H(("type", "log"))).Matched.Should().BeEmpty();
    }

    [Fact]
    public void Headers_x_arguments_are_ignored_unless_with_x()
    {
        var all = HeadersBinding("q1", ("x-match", "all"), ("x-tenant", "a"), ("format", "pdf"));
        var allWithX = HeadersBinding("q2", ("x-match", "all-with-x"), ("x-tenant", "a"), ("format", "pdf"));
        var anyOnlyX = HeadersBinding("q3", ("x-match", "any"), ("x-tenant", "a"));
        var anyWithX = HeadersBinding("q4", ("x-match", "any-with-x"), ("x-tenant", "a"));

        var preview = RoutingMatcher.Resolve(Headers, [all, allWithX, anyOnlyX, anyWithX], "", H(("format", "pdf")));
        preview.Matched.Should().Equal(all);

        var withTenant = RoutingMatcher.Resolve(Headers, [all, allWithX, anyOnlyX, anyWithX], "", H(("format", "pdf"), ("x-tenant", "a")));
        withTenant.Matched.Should().Equal(all, allWithX, anyWithX);
    }

    [Fact]
    public void Headers_values_compare_as_invariant_strings()
    {
        var b = HeadersBinding("q", ("urgent", true), ("count", 3L), ("ratio", 1.5d));

        RoutingMatcher.Resolve(Headers, [b], "", H(("urgent", "true"), ("count", "3"), ("ratio", "1.5"))).Matched.Should().Equal(b);
        RoutingMatcher.Resolve(Headers, [b], "", H(("urgent", "True"), ("count", "3"), ("ratio", "1.5"))).Matched.Should().BeEmpty();
    }

    [Fact]
    public void Headers_all_with_no_arguments_matches_everything_and_any_matches_nothing()
    {
        var all = HeadersBinding("q1");
        var any = HeadersBinding("q2", ("x-match", "any"));

        RoutingMatcher.Resolve(Headers, [all, any], "", NoHeaders).Matched.Should().Equal(all);
    }

    [Fact]
    public void Headers_no_match_offers_no_closest()
    {
        var b = HeadersBinding("q", ("format", "pdf"));
        RoutingMatcher.Resolve(Headers, [b], "format", NoHeaders).Closest.Should().BeEmpty();
    }

    [Fact]
    public void Exchange_to_exchange_bindings_match_like_any_other()
    {
        var e2e = Binding("t", "other.exchange", "a.#", destinationType: "exchange");
        RoutingMatcher.Resolve(Exchange("t", "topic"), [e2e], "a.b", NoHeaders).Matched.Should().Equal(e2e);
    }
}
