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
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 4, ActionCount: 5));
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
}
