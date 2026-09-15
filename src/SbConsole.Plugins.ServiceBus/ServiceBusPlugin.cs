using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus;

public sealed class ServiceBusPlugin : IPlugin
{
    public string Id => "azure-servicebus";
    public string DisplayName => "Azure Service Bus";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/azure-servicebus/queues"),
        new("Topics & Subscriptions", "/p/azure-servicebus/topics"),
        new("Dead-letter", DeadLetterNavHref),
    ];
    public string ConnectionKind => "azure-servicebus";
    public string ConnectionKindDisplayName => "Azure Service Bus";

    // Queues: Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter (6).
    // Topics & Subscriptions: Create/Delete topic, Create/Delete subscription, Peek subscription,
    // Resubmit/Purge subscription dead-letter (7 -- Send is reused, not counted again).
    // Pages: Queues, Topics & Subscriptions (combined), SubscriptionPeek, DeadLetterOverview.
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 13);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IServiceBusOperations, AzureServiceBusOperations>();
        services.AddScoped<Queues.ListQueuesQueryHandler>();
        services.AddScoped<Queues.CreateQueueCommandHandler>();
        services.AddScoped<Queues.DeleteQueueCommandHandler>();
        services.AddScoped<Messages.PeekMessagesQueryHandler>();
        services.AddScoped<Messages.SendMessageCommandHandler>();
        services.AddScoped<Messages.ResubmitDeadLetterMessagesCommandHandler>();
        services.AddScoped<Messages.PurgeDeadLetterMessagesCommandHandler>();
        services.AddScoped<Topics.ListTopicsQueryHandler>();
        services.AddScoped<Topics.CreateTopicCommandHandler>();
        services.AddScoped<Topics.DeleteTopicCommandHandler>();
        services.AddScoped<Subscriptions.CreateSubscriptionCommandHandler>();
        services.AddScoped<Subscriptions.DeleteSubscriptionCommandHandler>();
        services.AddScoped<Messages.PeekSubscriptionMessagesQueryHandler>();
        services.AddScoped<Messages.ResubmitSubscriptionDeadLetterMessagesCommandHandler>();
        services.AddScoped<Messages.PurgeSubscriptionDeadLetterMessagesCommandHandler>();
        services.AddScoped<DeadLetter.ListDeadLetterOverviewQueryHandler>();

        // Pre-bound to this plugin's own Id so pages (Queues.razor) can @inject IPluginStore
        // directly instead of going through the factory + this plugin's Id at every call site.
        services.AddScoped<IPluginStore>(sp => sp.GetRequiredService<IPluginStoreFactory>().For(Id));
    }

    private const string DeadLetterNavHref = "/p/azure-servicebus/dead-letter";

    // Constructs AzureServiceBusOperations directly, same as TestConnectionAsync above -- plugins
    // have no DI container at this layer (AddSbConsolePlugin<TPlugin>()'s `new()` constraint).
    public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        navItemHref == DeadLetterNavHref
            ? GetDeadLetterBadgeAsync(connectionString, ct)
            : Task.FromResult<int?>(null);

    private static async Task<int?> GetDeadLetterBadgeAsync(string connectionString, CancellationToken ct)
    {
        var entries = await new AzureServiceBusOperations().ListDeadLetterEntriesAsync(connectionString, ct);
        var total = entries.Sum(e => e.Count);
        return total > 0 ? (int)total : null;
    }

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        var ops = new AzureServiceBusOperations();
        var queues = await ops.ListQueuesAsync(connectionString, ct);
        var topics = await ops.ListTopicsAsync(connectionString, ct);
        var subscriptions = topics.Sum(t => t.SubscriptionCount);
        return
        [
            new PluginDashboardMetric("Queues", queues.Count),
            new PluginDashboardMetric("Topics", topics.Count),
            new PluginDashboardMetric("Subscriptions", subscriptions),
            new PluginDashboardMetric("Dead-lettered", (int)queues.Sum(q => q.DeadLetterMessageCount)),
        ];
    }

    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
    {
        var ops = new AzureServiceBusOperations();
        var queues = await ops.ListQueuesAsync(connectionString, ct);

        var historyByQueue = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>();
        foreach (var queue in queues.Where(q => q.DeadLetterMessageCount > 0))
        {
            historyByQueue[queue.Name] = await MetricHistoryStore.ReadAsync(store, connectionId, queue.Name, ct);
        }

        var problems = new List<PluginDashboardProblem>(
            DashboardProblems.ForDeadLetterBacklogs(queues, historyByQueue, DateTimeOffset.UtcNow));

        var topics = await ops.ListTopicsAsync(connectionString, ct);
        var subscriptionsByTopic = await Task.WhenAll(
            topics.Select(t => ops.ListSubscriptionsWithStatusAsync(connectionString, t.Name, ct)));

        for (var i = 0; i < topics.Count; i++)
        {
            problems.AddRange(DashboardProblems.ForDisabledSubscriptions(topics[i].Name, subscriptionsByTopic[i]));
        }

        return problems;
    }

    public async Task<OldestDeadLetterEntry?> GetOldestDeadLetterAsync(
        Guid connectionId, string connectionString, CancellationToken ct = default)
    {
        var ops = new AzureServiceBusOperations();
        var queues = await ops.ListQueuesAsync(connectionString, ct);
        var dlqQueues = queues.Where(q => q.DeadLetterMessageCount > 0).ToList();
        if (dlqQueues.Count == 0)
        {
            return null;
        }

        // One peek per DLQ-bearing queue, in parallel -- the exact fan-out shape the Dashboard
        // redesign's final review flagged for GetDashboardProblemsAsync's subscription fetch;
        // built parallel here from the start rather than serial-then-fixed. Bounded by a semaphore
        // so a namespace with many DLQ-bearing queues doesn't open that many simultaneous AMQP
        // connections on every wallboard load -- each PeekMessagesAsync call opens its own
        // ServiceBusClient (AzureServiceBusOperations), and this is the only caller unbounded here.
        const int MaxConcurrentPeeks = 8;
        using var throttle = new SemaphoreSlim(MaxConcurrentPeeks);
        var peeks = await Task.WhenAll(dlqQueues.Select(async q =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                return await ops.PeekMessagesAsync(connectionString, q.Name, fromDeadLetter: true, maxMessages: 1, fromSequenceNumber: null, ct);
            }
            finally
            {
                throttle.Release();
            }
        }));

        OldestDeadLetterEntry? oldest = null;
        for (var i = 0; i < dlqQueues.Count; i++)
        {
            var message = peeks[i].FirstOrDefault();
            if (message is null)
            {
                continue;
            }

            if (oldest is null || message.EnqueuedTime < oldest.EnqueuedTime)
            {
                oldest = new OldestDeadLetterEntry(dlqQueues[i].Name, message.EnqueuedTime, dlqQueues[i].DeadLetterMessageCount);
            }
        }

        return oldest;
    }

    public async Task<IReadOnlyList<PluginResourceMetric>> GetResourceMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        var queues = await new AzureServiceBusOperations().ListQueuesAsync(connectionString, ct);
        return [.. queues.Select(q => new PluginResourceMetric(q.Name, q.ActiveMessageCount, q.DeadLetterMessageCount))];
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered IServiceBusOperations from at
    // this layer — construct the real implementation directly, same as any other plugin would.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new AzureServiceBusOperations().TestConnectionAsync(secret, ct);
}
