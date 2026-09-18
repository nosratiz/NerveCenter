using FluentAssertions;
using SbConsole.Plugins.Kafka;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests;

public class KafkaPluginTests
{
    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new KafkaPlugin();

        plugin.Id.Should().Be("kafka");
        plugin.ConnectionKind.Should().Be("kafka");
        plugin.DisplayName.Should().Be("Apache Kafka");
        plugin.ConnectionKindDisplayName.Should().Be("Apache Kafka");
        plugin.NavItems.Should().Contain(n => n.Title == "Topics" && n.Href == "/p/kafka/topics");
        plugin.NavItems.Should().Contain(n => n.Title == "Consumer Groups" && n.Href == "/p/kafka/consumer-groups");
        plugin.NavItems.Should().Contain(n => n.Title == "Dead-letter" && n.Href == "/p/kafka/dead-letter");
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 5, ActionCount: 5));
    }

    [Fact]
    public async Task TestConnectionAsync_delegates_to_the_real_Kafka_client_and_never_throws()
    {
        var plugin = new KafkaPlugin();

        var result = await plugin.TestConnectionAsync("bootstrap.servers=127.0.0.1:1");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    // No real-broker smoke test for GetDashboardProblemsAsync (unlike TestConnectionAsync above):
    // verified empirically that Confluent.Kafka's ADMIN ASYNC API family (IAdminClient.
    // ListConsumerGroupsAsync/DescribeConsumerGroupsAsync/ListConsumerGroupOffsetsAsync -- what
    // ConfluentKafkaOperations.ListConsumerGroupsAsync, Task 2, calls under the hood) crashes the
    // process outright (SIGABRT/SIGSEGV on IAdminClient.Dispose or shortly after) on this
    // environment (macOS arm64, .NET 10, Confluent.Kafka 2.15.1), regardless of whether the call
    // succeeds or fails and regardless of the target address -- reproduced in a bare Confluent.Kafka
    // console app with zero SbConsole code involved, so this is a native-library/platform issue, not
    // something fixable here. TestConnectionAsync is unaffected because it only calls the
    // synchronous IAdminClient.GetMetadata. This also matches ServiceBusPlugin's own test suite,
    // which likewise has no real-broker smoke test for its GetDashboardProblemsAsync override --
    // only GetDashboardProblemsAsync's pure selection logic (HasHighLag below) needs unit coverage;
    // the thin ListConsumerGroupsAsync-then-Where/Select composition around it mirrors
    // GetDashboardMetricsAsync above, which likewise has no dedicated test.

    [Fact]
    public async Task GetNavBadgeAsync_returns_null_for_an_unrelated_nav_href()
    {
        var plugin = new KafkaPlugin();

        var badge = await plugin.GetNavBadgeAsync("/p/kafka/topics", "bootstrap.servers=127.0.0.1:1");

        badge.Should().BeNull();
    }

    [Theory]
    [InlineData(9_999, false)]
    [InlineData(10_000, false)]
    [InlineData(10_001, true)]
    public void HasHighLag_reflects_the_LagProblemThreshold_boundary(long totalLag, bool expected)
    {
        var group = new ConsumerGroupSummary("g", "Stable", 1, totalLag);

        KafkaPlugin.HasHighLag(group).Should().Be(expected);
    }

    // Regression coverage for the "dashboard problem link is a dead end" fix: ConsumerGroupDetail.razor
    // requires ?connectionId= to resolve the connection (without it, ConnectionId binds to Guid.Empty
    // and the page renders "Connection not found"), and Kafka group IDs are nearly unconstrained so the
    // group ID segment must be escaped -- same shape as ConsumerGroups.razor's own DetailUrl. Exercises
    // GetDashboardProblemsAsync itself (not a copy of its logic) by pre-seeding the ListConsumerGroups
    // cache for this connection string with a fake fetch, so GetDashboardProblemsAsync's own call to
    // GetCachedConsumerGroupsAsync hits the cache instead of the real broker (which crashes the process
    // on this environment -- see the comment above on GetDashboardProblemsAsync's missing smoke test).
    [Fact]
    public async Task GetDashboardProblemsAsync_links_to_the_group_detail_page_with_an_escaped_group_id_and_connectionId()
    {
        var plugin = new KafkaPlugin();
        var connectionId = Guid.NewGuid();
        var connectionString = $"conn-{connectionId}";
        var fetch = FakeFetch([new ConsumerGroupSummary("orders/consumer group", "Stable", 1, KafkaPlugin.LagProblemThreshold + 1)]);
        await KafkaPlugin.GetCachedConsumerGroupsAsync(connectionString, DateTimeOffset.UtcNow, fetch, CancellationToken.None);

        var problems = await plugin.GetDashboardProblemsAsync(connectionId, connectionString, store: null!);

        problems.Should().ContainSingle().Which.LinkHref.Should().Be(
            $"/p/kafka/consumer-groups/{Uri.EscapeDataString("orders/consumer group")}?connectionId={connectionId}");
    }

    [Fact]
    public async Task GetCachedConsumerGroupsAsync_reuses_the_result_for_a_repeat_call_within_the_TTL()
    {
        var connectionString = $"conn-{Guid.NewGuid()}";
        var callCount = 0;
        var groups = new List<ConsumerGroupSummary> { new("g1", "Stable", 1, 42) };
        Task<IReadOnlyList<ConsumerGroupSummary>> Fetch(string cs, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<ConsumerGroupSummary>>(groups);
        }

        var now = DateTimeOffset.UtcNow;
        var first = await KafkaPlugin.GetCachedConsumerGroupsAsync(connectionString, now, Fetch, CancellationToken.None);
        // 30 seconds later, still within the ~60s TTL -- should reuse the cached result, not fetch again.
        var second = await KafkaPlugin.GetCachedConsumerGroupsAsync(connectionString, now.AddSeconds(30), Fetch, CancellationToken.None);

        callCount.Should().Be(1);
        first.Should().BeSameAs(groups);
        second.Should().BeSameAs(groups);
    }

    [Fact]
    public async Task GetCachedConsumerGroupsAsync_fetches_again_once_the_TTL_has_expired()
    {
        var connectionString = $"conn-{Guid.NewGuid()}";
        var callCount = 0;
        Task<IReadOnlyList<ConsumerGroupSummary>> Fetch(string cs, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<ConsumerGroupSummary>>([new ConsumerGroupSummary("g", "Stable", 1, callCount)]);
        }

        var now = DateTimeOffset.UtcNow;
        await KafkaPlugin.GetCachedConsumerGroupsAsync(connectionString, now, Fetch, CancellationToken.None);
        // 61 seconds later -- past the ~60s TTL -- should fetch a fresh result.
        await KafkaPlugin.GetCachedConsumerGroupsAsync(connectionString, now.AddSeconds(61), Fetch, CancellationToken.None);

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task GetCachedConsumerGroupsAsync_caches_independently_per_connection_string()
    {
        var connectionA = $"conn-a-{Guid.NewGuid()}";
        var connectionB = $"conn-b-{Guid.NewGuid()}";
        var callCount = 0;
        Task<IReadOnlyList<ConsumerGroupSummary>> Fetch(string cs, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<ConsumerGroupSummary>>([]);
        }

        var now = DateTimeOffset.UtcNow;
        await KafkaPlugin.GetCachedConsumerGroupsAsync(connectionA, now, Fetch, CancellationToken.None);
        await KafkaPlugin.GetCachedConsumerGroupsAsync(connectionB, now, Fetch, CancellationToken.None);

        callCount.Should().Be(2);
    }

    private static Func<string, CancellationToken, Task<IReadOnlyList<ConsumerGroupSummary>>> FakeFetch(IReadOnlyList<ConsumerGroupSummary> groups) =>
        (_, _) => Task.FromResult(groups);
}
