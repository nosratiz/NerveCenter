using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;
using static SbConsole.Plugins.RabbitMq.Tests.Routing.RoutingFixtures;

namespace SbConsole.Plugins.RabbitMq.Tests.Routing;

public class PolicyMatcherTests
{
    private static readonly QueueSummary[] Queues =
    [
        Queue("order-events.q", type: "quorum"),
        Queue("billing.payments.q"),
        Queue("billing.retry"),
        Queue("payments-dlq"),
        Queue("audit.sink"),
        Queue("clicks", type: "stream"),
    ];

    private static readonly ExchangeSummary[] Exchanges =
    [
        Exchange("", "direct"),
        Exchange("amq.direct", "direct"),
        Exchange("order-events", "topic"),
        Exchange("billing.retry.dlx", "direct"),
        Exchange("billing.direct", "direct"),
    ];

    [Theory]
    [InlineData("orders-limits", "^order-events", "queues", 1)]
    [InlineData("dlq-ttl", "-dlq$", "queues", 1)]
    [InlineData("retry-shortttl", @"\.retry$", "queues", 1)]
    [InlineData("everything", ".*", "queues", 6)]
    [InlineData("quorum-only", ".*", "quorum_queues", 1)]
    [InlineData("classic-only", ".*", "classic_queues", 4)]
    [InlineData("streams-only", ".*", "streams", 1)]
    [InlineData("exchanges", ".*", "exchanges", 4)] // default "" exchange excluded
    [InlineData("exchanges-billing", "^billing", "exchanges", 2)]
    [InlineData("all", "^order-events", "all", 2)]
    public void CountMatches_by_apply_to(string name, string pattern, string applyTo, int expected) =>
        PolicyMatcher.CountMatches(Policy(name, pattern, applyTo), Queues, Exchanges).Should().Be(expected);

    [Theory]
    [InlineData("(unclosed")]
    [InlineData("[z-a]")]
    public void CountMatches_invalid_regex_is_null(string pattern) =>
        PolicyMatcher.CountMatches(Policy("bad", pattern, "queues"), Queues, Exchanges).Should().BeNull();

    [Fact]
    public void CountMatches_unknown_apply_to_is_null() =>
        PolicyMatcher.CountMatches(Policy("odd", ".*", "vhosts"), Queues, Exchanges).Should().BeNull();

    [Fact]
    public void CountMatches_catastrophic_backtracking_times_out_to_null()
    {
        var evil = Queue(new string('a', 40) + "!");
        PolicyMatcher.CountMatches(Policy("evil", "^(a+)+$", "queues"), [evil], []).Should().BeNull();
    }

    [Fact]
    public void WinningPolicy_highest_priority_queue_applicable_match()
    {
        var q = Queue("order-events.q", type: "quorum");
        var policies = new[]
        {
            Policy("low", "^order", "queues", 0),
            Policy("high", "^order", "all", 5),
            Policy("exchanges-only", "^order", "exchanges", 99),
            Policy("classic-only", "^order", "classic_queues", 50),
            Policy("no-match", "^billing", "queues", 100),
            Policy("broken", "(", "queues", 200),
        };

        PolicyMatcher.WinningPolicy(q, policies)!.Name.Should().Be("high");
    }

    [Fact]
    public void WinningPolicy_tie_breaks_by_name_ordinal()
    {
        var q = Queue("payments-dlq");
        var policies = new[] { Policy("zeta", "dlq", "queues", 3), Policy("alpha", "dlq", "queues", 3) };

        PolicyMatcher.WinningPolicy(q, policies)!.Name.Should().Be("alpha");
    }

    [Fact]
    public void WinningPolicy_none_matching_is_null() =>
        PolicyMatcher.WinningPolicy(Queue("audit.sink"), [Policy("p", "^order", "queues")]).Should().BeNull();
}
