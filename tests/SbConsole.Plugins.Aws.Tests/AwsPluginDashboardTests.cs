using FluentAssertions;
using SbConsole.Plugins.Aws;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests;

// Covers the dashboard/badge/resource-metric hooks through their internal static helpers over
// IReadOnlyList<QueueSummary> -- the public IPlugin entry points always seed the cache via the real
// SqsOperations fetch, which needs AWS, so (same as KafkaPluginTests) only the pure derivation logic
// and the cache's reuse/expiry behavior are exercised here.
public class AwsPluginDashboardTests
{
    private static QueueSummary Queue(string name, long visible, int deadLetterSources = 0, bool unavailable = false) =>
        unavailable
            ? QueueSummary.Unavailable(name, $"https://sqs.us-east-1.amazonaws.com/123456789012/{name}")
            : new QueueSummary(
                Name: name,
                QueueUrl: $"https://sqs.us-east-1.amazonaws.com/123456789012/{name}",
                QueueArn: $"arn:aws:sqs:us-east-1:123456789012:{name}",
                IsFifo: false,
                ApproxVisible: visible, ApproxInFlight: 0, ApproxDelayed: 0,
                HasDeadLetterTarget: false, IsKmsEncrypted: false, CreatedAt: DateTimeOffset.UnixEpoch,
                DeadLetterSourceCount: deadLetterSources);

    [Fact]
    public async Task GetCachedQueuesAsync_reuses_the_result_for_a_repeat_call_within_the_TTL()
    {
        var connectionString = $"conn-{Guid.NewGuid()}";
        var callCount = 0;
        var queues = new List<QueueSummary> { Queue("orders", 3) };
        Task<IReadOnlyList<QueueSummary>> Fetch(string cs, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<QueueSummary>>(queues);
        }

        var now = DateTimeOffset.UtcNow;
        var first = await AwsPlugin.GetCachedQueuesAsync(connectionString, now, Fetch, CancellationToken.None);
        var second = await AwsPlugin.GetCachedQueuesAsync(connectionString, now.AddSeconds(30), Fetch, CancellationToken.None);

        callCount.Should().Be(1);
        first.Should().BeSameAs(queues);
        second.Should().BeSameAs(queues);
    }

    [Fact]
    public async Task GetCachedQueuesAsync_fetches_again_once_the_TTL_has_expired()
    {
        var connectionString = $"conn-{Guid.NewGuid()}";
        var callCount = 0;
        Task<IReadOnlyList<QueueSummary>> Fetch(string cs, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<QueueSummary>>([Queue("q", callCount)]);
        }

        var now = DateTimeOffset.UtcNow;
        await AwsPlugin.GetCachedQueuesAsync(connectionString, now, Fetch, CancellationToken.None);
        var refreshed = await AwsPlugin.GetCachedQueuesAsync(connectionString, now.AddSeconds(61), Fetch, CancellationToken.None);

        callCount.Should().Be(2);
        refreshed.Single().ApproxVisible.Should().Be(2);
    }

    [Fact]
    public async Task GetCachedQueuesAsync_caches_independently_per_connection_string()
    {
        var callCount = 0;
        Task<IReadOnlyList<QueueSummary>> Fetch(string cs, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<QueueSummary>>([]);
        }

        var now = DateTimeOffset.UtcNow;
        await AwsPlugin.GetCachedQueuesAsync($"conn-a-{Guid.NewGuid()}", now, Fetch, CancellationToken.None);
        await AwsPlugin.GetCachedQueuesAsync($"conn-b-{Guid.NewGuid()}", now, Fetch, CancellationToken.None);

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task GetCachedQueuesAsync_does_not_cache_a_failed_fetch()
    {
        var connectionString = $"conn-{Guid.NewGuid()}";
        var callCount = 0;
        Task<IReadOnlyList<QueueSummary>> Fetch(string cs, CancellationToken ct)
        {
            callCount++;
            return callCount == 1
                ? Task.FromException<IReadOnlyList<QueueSummary>>(new InvalidOperationException("throttled"))
                : Task.FromResult<IReadOnlyList<QueueSummary>>([Queue("q", 1)]);
        }

        var now = DateTimeOffset.UtcNow;
        var failing = () => AwsPlugin.GetCachedQueuesAsync(connectionString, now, Fetch, CancellationToken.None);
        await failing.Should().ThrowAsync<InvalidOperationException>();

        var retried = await AwsPlugin.GetCachedQueuesAsync(connectionString, now.AddSeconds(1), Fetch, CancellationToken.None);

        callCount.Should().Be(2);
        retried.Should().ContainSingle();
    }

    [Fact]
    public void ComputeDeadLetterBadge_sums_visible_messages_across_dead_letter_queues_only()
    {
        IReadOnlyList<QueueSummary> queues =
        [
            Queue("orders", visible: 100),
            Queue("orders-dlq", visible: 4, deadLetterSources: 1),
            Queue("payments-dlq", visible: 6, deadLetterSources: 2),
        ];

        AwsPlugin.ComputeDeadLetterBadge(queues).Should().Be(10);
    }

    [Fact]
    public void ComputeDeadLetterBadge_is_null_rather_than_zero_when_no_dead_letter_queue_has_messages()
    {
        IReadOnlyList<QueueSummary> queues =
        [
            Queue("orders", visible: 100),
            Queue("orders-dlq", visible: 0, deadLetterSources: 1),
        ];

        AwsPlugin.ComputeDeadLetterBadge(queues).Should().BeNull();
        AwsPlugin.ComputeDeadLetterBadge([]).Should().BeNull();
    }

    [Fact]
    public void BuildDashboardMetrics_reports_queue_count_and_dead_lettered_total()
    {
        IReadOnlyList<QueueSummary> queues =
        [
            Queue("orders", visible: 100),
            Queue("orders-dlq", visible: 4, deadLetterSources: 1),
            Queue("broken", visible: 0, unavailable: true),
        ];

        AwsPlugin.BuildDashboardMetrics(queues).Should().Equal(
            new PluginDashboardMetric("Queues", 3),
            new PluginDashboardMetric("Dead-lettered", 4));
    }

    [Fact]
    public void BuildResourceMetrics_reports_one_reading_per_readable_queue()
    {
        IReadOnlyList<QueueSummary> queues =
        [
            Queue("orders", visible: 100),
            Queue("orders-dlq", visible: 4, deadLetterSources: 1),
            Queue("broken", visible: 0, unavailable: true),
        ];

        AwsPlugin.BuildResourceMetrics(queues).Should().Equal(
            new PluginResourceMetric("orders", ActiveCount: 100, DeadLetterCount: 0),
            new PluginResourceMetric("orders-dlq", ActiveCount: 4, DeadLetterCount: 4));
    }

    [Fact]
    public void BuildDashboardProblems_flags_each_dead_letter_queue_with_visible_messages()
    {
        var connectionId = Guid.NewGuid();
        var dlq = Queue("orders-dlq", visible: 1234, deadLetterSources: 1);
        IReadOnlyList<QueueSummary> queues =
        [
            Queue("orders", visible: 100),
            dlq,
            Queue("empty-dlq", visible: 0, deadLetterSources: 1),
        ];

        var problems = AwsPlugin.BuildDashboardProblems(connectionId, queues);

        problems.Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Warning",
            "orders-dlq",
            "1,234 dead-lettered",
            AwsPlugin.BuildQueueProblemLink(connectionId, dlq.QueueUrl)));
    }

    [Fact]
    public void BuildQueueProblemLink_escapes_the_queue_url_and_carries_connectionId()
    {
        var connectionId = Guid.NewGuid();
        const string queueUrl = "https://sqs.us-east-1.amazonaws.com/123456789012/orders-dlq";

        var link = AwsPlugin.BuildQueueProblemLink(connectionId, queueUrl);

        link.Should().Be($"/p/aws/queues/{Uri.EscapeDataString(queueUrl)}?connectionId={connectionId}");
        link.Should().NotContain("https://");
    }
}
