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

    [Fact]
    public void PickOldestDeadLetter_picks_the_queue_with_the_largest_age_and_derives_EnqueuedTime_from_now()
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var result = AwsPlugin.PickOldestDeadLetter(
            [
                (Queue("a-dlq", 3, deadLetterSources: 1), TimeSpan.FromMinutes(5)),
                (Queue("b-dlq", 7, deadLetterSources: 1), TimeSpan.FromHours(2)),
                (Queue("c-dlq", 11, deadLetterSources: 1), TimeSpan.FromMinutes(30)),
            ],
            now);

        // DeadLetterCount is the chosen queue's own count, not a total across DLQs -- same
        // semantics as KafkaPlugin/ServiceBusPlugin.
        result.Should().Be(new OldestDeadLetterEntry("b-dlq", now - TimeSpan.FromHours(2), 7));
    }

    [Fact]
    public void PickOldestDeadLetter_skips_queues_without_an_age()
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var result = AwsPlugin.PickOldestDeadLetter(
            [
                (Queue("no-metrics-dlq", 50, deadLetterSources: 1), null),
                (Queue("b-dlq", 7, deadLetterSources: 1), TimeSpan.FromSeconds(90)),
            ],
            now);

        result.Should().Be(new OldestDeadLetterEntry("b-dlq", now.AddSeconds(-90), 7));
    }

    [Fact]
    public void PickOldestDeadLetter_is_null_when_no_queue_has_an_age()
    {
        AwsPlugin.PickOldestDeadLetter([(Queue("a-dlq", 3, deadLetterSources: 1), null)], DateTimeOffset.UtcNow)
            .Should().BeNull();
        AwsPlugin.PickOldestDeadLetter([], DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public async Task ComputeOldestDeadLetterAsync_queries_only_dead_letter_queues_with_messages()
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var queried = new List<string>();
        Task<TimeSpan?> GetAge(string queueName, CancellationToken ct)
        {
            lock (queried)
            {
                queried.Add(queueName);
            }

            return Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(queueName.Length));
        }

        var result = await AwsPlugin.ComputeOldestDeadLetterAsync(
            [
                Queue("orders", 100),
                Queue("empty-dlq", 0, deadLetterSources: 1),
                Queue("orders-dlq", 4, deadLetterSources: 1),
                Queue("broken-dlq", 0, deadLetterSources: 1, unavailable: true),
            ],
            now, GetAge, CancellationToken.None);

        queried.Should().BeEquivalentTo(["orders-dlq"]);
        result.Should().Be(new OldestDeadLetterEntry("orders-dlq", now - TimeSpan.FromMinutes("orders-dlq".Length), 4));
    }

    [Fact]
    public async Task ComputeOldestDeadLetterAsync_returns_null_without_any_metric_call_when_no_dead_letter_queue_has_messages()
    {
        var calls = 0;
        var result = await AwsPlugin.ComputeOldestDeadLetterAsync(
            [Queue("orders", 100), Queue("orders-dlq", 0, deadLetterSources: 1)],
            DateTimeOffset.UtcNow,
            (_, _) => { calls++; return Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(1)); },
            CancellationToken.None);

        result.Should().BeNull();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task ComputeOldestDeadLetterAsync_skips_a_single_failing_queue_when_another_succeeds()
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var result = await AwsPlugin.ComputeOldestDeadLetterAsync(
            [Queue("a-dlq", 2, deadLetterSources: 1), Queue("b-dlq", 5, deadLetterSources: 1)],
            now,
            (name, _) => name == "a-dlq"
                ? Task.FromException<TimeSpan?>(new InvalidOperationException("throttled"))
                : Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(10)),
            CancellationToken.None);

        result.Should().Be(new OldestDeadLetterEntry("b-dlq", now.AddMinutes(-10), 5));
    }

    [Fact]
    public async Task ComputeOldestDeadLetterAsync_propagates_when_every_metric_call_fails()
    {
        var act = () => AwsPlugin.ComputeOldestDeadLetterAsync(
            [Queue("a-dlq", 2, deadLetterSources: 1), Queue("b-dlq", 5, deadLetterSources: 1)],
            DateTimeOffset.UtcNow,
            (_, _) => Task.FromException<TimeSpan?>(new InvalidOperationException("AccessDenied")),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("AccessDenied");
    }
}
