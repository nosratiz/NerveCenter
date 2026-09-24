using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws;

public sealed class AwsPlugin : IPlugin
{
    public string Id => "aws";
    public string DisplayName => "AWS SQS/SNS";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/aws/queues"),
        new("Topics", "/p/aws/topics"),
    ];
    public string ConnectionKind => "aws";
    public string ConnectionKindDisplayName => "AWS SQS/SNS";

    public Type? ConnectionFormComponentType => typeof(AwsConnectionFields);

    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        return new Dictionary<string, string> { ["Region"] = parsed.GetValueOrDefault("region", "?") };
    }

    // Queues: Create/Delete/Purge queue, Receive, Delete message, Release message, Send, Redrive (8).
    // Topics: Create/Delete topic, Subscribe/Unsubscribe, Publish (5).
    // Pages: Queues, QueueDetail, Receive, Topics, TopicDetail (5). QueueDetail adds no new action --
    // its Send/Purge/Delete buttons reuse the existing handlers above.
    public PluginContribution Contribution => new(PageCount: 5, ActionCount: 13);

    // The Queues/Messages/Redrive command and query handlers are added to this project one at a
    // time by Tasks 4, 7, 8, 9, 10, 12, 13 -- registering them here in Task 3 (as the plan's
    // AwsPlugin.cs listing does) would reference types that don't exist yet and fail the build.
    // Each of those later tasks adds its own `services.AddScoped<...>()` line here as it creates
    // the corresponding handler class.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISqsOperations, SqsOperations>();
        services.AddSingleton<ISnsOperations, SnsOperations>();
        services.AddScoped<ListQueuesQueryHandler>();
        services.AddScoped<GetQueueDetailQueryHandler>();
        services.AddScoped<ListQueueSnsSubscriptionsQueryHandler>();
        services.AddScoped<ListTopicsQueryHandler>();
        services.AddScoped<CreateTopicCommandHandler>();
        services.AddScoped<DeleteTopicCommandHandler>();
        services.AddScoped<ListSubscriptionsQueryHandler>();
        services.AddScoped<SubscribeCommandHandler>();
        services.AddScoped<UnsubscribeCommandHandler>();
        services.AddScoped<PublishCommandHandler>();
        services.AddScoped<GetSubscriptionFilterPoliciesQueryHandler>();
        services.AddScoped<GetTopicDeliveryFailureCountQueryHandler>();
        services.AddScoped<GetConnectionEchoQueryHandler>();
        services.AddScoped<Queues.DeleteQueueCommandHandler>();
        services.AddScoped<Queues.CreateQueueCommandHandler>();
        services.AddScoped<Queues.PurgeQueueCommandHandler>();
        services.AddScoped<Messages.ReceiveMessagesCommandHandler>();
        services.AddScoped<Messages.DeleteMessageCommandHandler>();
        services.AddScoped<Messages.ReleaseMessageCommandHandler>();
        services.AddScoped<Messages.SendMessageCommandHandler>();
        services.AddScoped<Redrive.StartRedriveCommandHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered ISqsOperations from at this
    // layer -- construct the real implementation directly, same as KafkaPlugin/ServiceBusPlugin.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new SqsOperations().TestConnectionAsync(secret, ct);

    // Short-lived cache for ListQueuesAsync's result, shared by GetNavBadgeAsync,
    // GetDashboardMetricsAsync, GetDashboardProblemsAsync and GetResourceMetricsAsync -- built exactly
    // like KafkaPlugin.GetCachedConsumerGroupsAsync. ListQueuesAsync is ListQueues plus one
    // GetQueueAttributes round-trip per queue, and NavMenu.razor polls the badge every 60s per open
    // circuit, Home.razor/the Wallboard call the dashboard hooks on every render/refresh, and
    // MetricsCollectorService calls GetResourceMetricsAsync periodically -- without this cache the same
    // per-queue walk would repeat several times per cycle over identical data. `static` because
    // AwsPlugin instances are constructed fresh via new() on every call (see TestConnectionAsync), so
    // a static field is the only way to share the result across these call paths. The secret is
    // already held in memory as a plain parameter throughout this file, so keying on it adds no new
    // exposure. Deliberately NOT used by ListQueuesQueryHandler -- the Queues page's explicit load
    // stays fresh. A failed fetch throws before the cache is written, so failures are never cached.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, IReadOnlyList<QueueSummary> Queues)> QueuesCache = new();

    private static readonly TimeSpan QueuesCacheTtl = TimeSpan.FromSeconds(60);

    private static Task<IReadOnlyList<QueueSummary>> GetCachedQueuesAsync(string connectionString, CancellationToken ct) =>
        GetCachedQueuesAsync(
            connectionString,
            DateTimeOffset.UtcNow,
            static (cs, t) => new SqsOperations().ListQueuesAsync(cs, null, t),
            ct);

    // `now` and `fetch` are parameters so the cache's reuse/expiry behavior is unit-testable with a
    // counting fake fetcher and a controlled clock, without AWS -- same as KafkaPlugin's overloads.
    internal static async Task<IReadOnlyList<QueueSummary>> GetCachedQueuesAsync(
        string connectionString,
        DateTimeOffset now,
        Func<string, CancellationToken, Task<IReadOnlyList<QueueSummary>>> fetch,
        CancellationToken ct)
    {
        if (QueuesCache.TryGetValue(connectionString, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Queues;
        }

        var queues = await fetch(connectionString, ct);
        QueuesCache[connectionString] = (now + QueuesCacheTtl, queues);
        return queues;
    }

    // A queue is a dead-letter queue when other queues' RedrivePolicy targets it -- NOT when it has a
    // RedrivePolicy of its own (see QueueSummary.HasDeadLetterTarget's comment). An
    // AttributesUnavailable row never qualifies: its counts are meaningless defaults.
    internal static bool IsDeadLetterQueue(QueueSummary queue) =>
        !queue.AttributesUnavailable && queue.DeadLetterSourceCount > 0;

    private static long DeadLetteredTotal(IReadOnlyList<QueueSummary> queues) =>
        queues.Where(IsDeadLetterQueue).Sum(q => q.ApproxVisible);

    private static int ClampToInt(long value) => (int)Math.Min(value, int.MaxValue);

    internal static int? ComputeDeadLetterBadge(IReadOnlyList<QueueSummary> queues)
    {
        var total = DeadLetteredTotal(queues);
        // null (never 0) when nothing is dead-lettered -- NavMenu.razor renders a visible "0" badge
        // for a literal 0 and only omits the badge for null (same rule KafkaPlugin follows).
        return total > 0 ? ClampToInt(total) : null;
    }

    internal static IReadOnlyList<PluginDashboardMetric> BuildDashboardMetrics(IReadOnlyList<QueueSummary> queues) =>
    [
        new PluginDashboardMetric("Queues", queues.Count),
        // "Dead-lettered" is the label WallboardSnapshotLoader sums into its per-namespace backlog.
        new PluginDashboardMetric("Dead-lettered", ClampToInt(DeadLetteredTotal(queues))),
    ];

    internal static IReadOnlyList<PluginResourceMetric> BuildResourceMetrics(IReadOnlyList<QueueSummary> queues) =>
        queues
            .Where(q => !q.AttributesUnavailable)
            .Select(q => new PluginResourceMetric(q.Name, q.ApproxVisible, IsDeadLetterQueue(q) ? q.ApproxVisible : 0))
            .ToList();

    internal static IReadOnlyList<PluginDashboardProblem> BuildDashboardProblems(Guid connectionId, IReadOnlyList<QueueSummary> queues) =>
        queues
            .Where(q => IsDeadLetterQueue(q) && q.ApproxVisible > 0)
            .Select(q => new PluginDashboardProblem(
                "Warning",
                q.Name,
                string.Create(CultureInfo.InvariantCulture, $"{q.ApproxVisible:N0} dead-lettered"),
                BuildQueueProblemLink(connectionId, q.QueueUrl)))
            .ToList();

    // Carries ?connectionId= for the same reason as KafkaPlugin.BuildConsumerGroupProblemLink: plugin
    // detail pages resolve their connection from that query parameter (TopicDetail.razor/
    // Receive.razor do), so a link without it would be a dead end. The queue URL is a full https URL,
    // so it must be escaped into a single route segment -- same approach as TopicDetail's ARN route.
    internal static string BuildQueueProblemLink(Guid connectionId, string queueUrl) =>
        $"{QueuesNavHref}/{Uri.EscapeDataString(queueUrl)}?connectionId={connectionId}";

    private const string QueuesNavHref = "/p/aws/queues";

    // Failures (unreachable endpoint, denied credentials, throttling) deliberately propagate, exactly
    // like KafkaPlugin's hooks: every host call site (NavMenu.razor, Home.razor,
    // WallboardSnapshotLoader, MetricsCollectorService) already wraps these calls in try/catch and
    // logs, and Home/Wallboard use the exception to mark the connection "unchecked" -- swallowing it
    // here and returning empty would instead show a broken connection as healthy.
    public async Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        navItemHref == QueuesNavHref
            ? ComputeDeadLetterBadge(await GetCachedQueuesAsync(connectionString, ct))
            : null;

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default) =>
        BuildDashboardMetrics(await GetCachedQueuesAsync(connectionString, ct));

    public async Task<IReadOnlyList<PluginResourceMetric>> GetResourceMetricsAsync(string connectionString, CancellationToken ct = default) =>
        BuildResourceMetrics(await GetCachedQueuesAsync(connectionString, ct));

    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
        BuildDashboardProblems(connectionId, await GetCachedQueuesAsync(connectionString, ct));

    // GetOldestDeadLetterAsync: SDK default (null). SQS exposes no enqueue timestamp without
    // *receiving* a message, and a receive has a real side effect (the message goes invisible), so
    // the wallboard's "Oldest message" tile is deliberately not fed by this plugin.
}
