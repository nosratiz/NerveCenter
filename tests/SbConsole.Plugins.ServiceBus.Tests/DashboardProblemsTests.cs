using FluentAssertions;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests;

public class DashboardProblemsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T09:41:00Z");

    [Fact]
    public void ForDeadLetterBacklogs_ignores_queues_with_no_dead_letters()
    {
        var queues = new[] { new QueueSummary("orders-inbound", 10, 0, 0, 0) };

        var problems = DashboardProblems.ForDeadLetterBacklogs(
            queues, new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>(), Now);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void ForDeadLetterBacklogs_reports_the_current_count_with_no_delta_when_there_is_no_hour_old_history()
    {
        var queues = new[] { new QueueSummary("payments-dlq", 0, 214, 0, 0) };

        var problems = DashboardProblems.ForDeadLetterBacklogs(
            queues, new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>(), Now);

        problems.Should().ContainSingle().Which.Should().Be(
            new PluginDashboardProblem("Warning", "payments-dlq", "214 dead-lettered", "/p/azure-servicebus/dead-letter"));
    }

    [Fact]
    public void ForDeadLetterBacklogs_appends_the_growth_delta_when_an_hour_old_point_exists()
    {
        var queues = new[] { new QueueSummary("payments-dlq", 0, 214, 0, 0) };
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>
        {
            ["payments-dlq"] =
            [
                new MetricSnapshotPoint(Now.AddHours(-2), 0, 100),
                new MetricSnapshotPoint(Now.AddHours(-1), 0, 176), // closest point at/before the 1h cutoff
                new MetricSnapshotPoint(Now.AddMinutes(-10), 0, 210), // too recent to count as the baseline
            ],
        };

        var problems = DashboardProblems.ForDeadLetterBacklogs(queues, history, Now);

        problems.Should().ContainSingle().Which.Detail.Should().Be("214 dead-lettered, +38 in the last hour");
    }

    [Fact]
    public void ForDeadLetterBacklogs_omits_the_delta_when_the_backlog_shrank_or_held_steady()
    {
        var queues = new[] { new QueueSummary("payments-dlq", 0, 50, 0, 0) };
        var history = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>
        {
            ["payments-dlq"] = [new MetricSnapshotPoint(Now.AddHours(-1), 0, 80)],
        };

        var problems = DashboardProblems.ForDeadLetterBacklogs(queues, history, Now);

        problems.Should().ContainSingle().Which.Detail.Should().Be("50 dead-lettered");
    }

    [Fact]
    public void ForDisabledSubscriptions_ignores_active_subscriptions()
    {
        var subscriptions = new[] { new SubscriptionSummary("sms", 0, 0, 0, "Active") };

        var problems = DashboardProblems.ForDisabledSubscriptions("notify-fanout", subscriptions);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void ForDisabledSubscriptions_reports_every_non_active_subscription()
    {
        var subscriptions = new[]
        {
            new SubscriptionSummary("sms", 0, 0, 0, "Disabled"),
            new SubscriptionSummary("email", 0, 0, 0, "Active"),
            new SubscriptionSummary("push", 0, 0, 0, "ReceiveDisabled"),
        };

        var problems = DashboardProblems.ForDisabledSubscriptions("notify-fanout", subscriptions);

        problems.Should().BeEquivalentTo(
        [
            new PluginDashboardProblem("Warning", "notify-fanout / sms", "subscription disabled", "/p/azure-servicebus/topics"),
            new PluginDashboardProblem("Warning", "notify-fanout / push", "subscription receivedisabled", "/p/azure-servicebus/topics"),
        ]);
    }
}
