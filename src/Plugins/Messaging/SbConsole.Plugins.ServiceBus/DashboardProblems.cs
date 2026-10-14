using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus;

/// <summary>
/// Turns already-fetched Service Bus data into Dashboard "Needs attention" problems. Pure and
/// synchronous on purpose: ServiceBusPlugin's own IPlugin methods construct AzureServiceBusOperations
/// directly with no DI seam and can't be meaningfully unit-tested without a live broker (see
/// AzureServiceBusOperations' own header comment) -- this class is the actual test leverage for the
/// dashboard-problem logic, the same way MetricHistoryStore is the test leverage for history.
/// </summary>
internal static class DashboardProblems
{
    private static readonly TimeSpan DeltaWindow = TimeSpan.FromHours(1);
    private const string DeadLetterNavHref = "/p/azure-servicebus/dead-letter";
    private const string TopicsNavHref = "/p/azure-servicebus/topics";

    public static IReadOnlyList<PluginDashboardProblem> ForDeadLetterBacklogs(
        IReadOnlyList<QueueSummary> queues,
        IReadOnlyDictionary<string, IReadOnlyList<MetricSnapshotPoint>> historyByQueue,
        DateTimeOffset now)
    {
        var cutoff = now - DeltaWindow;
        var problems = new List<PluginDashboardProblem>();
        foreach (var queue in queues.Where(q => q.DeadLetterMessageCount > 0))
        {
            var detail = $"{queue.DeadLetterMessageCount} dead-lettered";
            if (historyByQueue.TryGetValue(queue.Name, out var history))
            {
                var baseline = history.Where(p => p.At <= cutoff).OrderByDescending(p => p.At).FirstOrDefault();
                if (baseline is not null)
                {
                    var delta = queue.DeadLetterMessageCount - baseline.DeadLetterCount;
                    if (delta > 0)
                    {
                        detail += $", +{delta} in the last hour";
                    }
                }
            }

            problems.Add(new PluginDashboardProblem("Warning", queue.Name, detail, DeadLetterNavHref));
        }

        return problems;
    }

    public static IReadOnlyList<PluginDashboardProblem> ForDisabledSubscriptions(
        string topicName, IReadOnlyList<SubscriptionSummary> subscriptions) =>
        [
            // Explicit allowlist, not "!= Active": Azure's real EntityStatus enum also includes
            // transient provisioning states (Creating, Deleting, Renaming, Restoring, Unknown) that
            // are not problems -- a subscription mid-provision would otherwise render a false-
            // positive "subscription creating" Warning card.
            .. subscriptions
                .Where(s => s.Status is "Disabled" or "ReceiveDisabled" or "SendDisabled")
                .Select(s => new PluginDashboardProblem(
                    "Warning", $"{topicName} / {s.Name}", $"subscription {s.Status.ToLowerInvariant()}", TopicsNavHref))
        ];
}
