using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka;

public sealed class KafkaPlugin : IPlugin
{
    // Shared by ConsumerGroups.razor's warning chip (Task 6) and GetDashboardProblemsAsync/
    // GetNavBadgeAsync (Task 8) so the threshold is defined exactly once. See design spec §6.
    internal const long LagProblemThreshold = 10_000;

    public string Id => "kafka";
    public string DisplayName => "Apache Kafka";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Topics", "/p/kafka/topics"),
        new("Consumer Groups", "/p/kafka/consumer-groups"),
        new("Dead-letter", "/p/kafka/dead-letter"),
    ];
    public string ConnectionKind => "kafka";
    public string ConnectionKindDisplayName => "Apache Kafka";

    // Topics: Create/Delete topic, Peek, Produce, Reset offset (5).
    // Pages: Topics, Peek, ConsumerGroups, ConsumerGroupDetail, DeadLetterOverview (5).
    public PluginContribution Contribution => new(PageCount: 5, ActionCount: 5);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IKafkaOperations, ConfluentKafkaOperations>();
        services.AddScoped<Topics.ListTopicsQueryHandler>();
        services.AddScoped<Topics.CreateTopicCommandHandler>();
        services.AddScoped<Topics.DeleteTopicCommandHandler>();
        services.AddScoped<Topics.GetConnectionEchoQueryHandler>();
        services.AddScoped<Messages.PeekMessagesQueryHandler>();
        services.AddScoped<Messages.ProduceMessageCommandHandler>();
        services.AddScoped<ConsumerGroups.ListConsumerGroupsQueryHandler>();
        services.AddScoped<ConsumerGroups.GetConsumerGroupDetailQueryHandler>();
        services.AddScoped<ConsumerGroups.ResetConsumerGroupOffsetCommandHandler>();
        services.AddScoped<DeadLetter.ListDeadLetterOverviewQueryHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered IKafkaOperations from at this
    // layer -- construct the real implementation directly, same as ServiceBusPlugin does.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new ConfluentKafkaOperations().TestConnectionAsync(secret, ct);

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        // GetTopicCountsAsync, not ListTopicsAsync -- this runs on every Dashboard render and every
        // Wallboard refresh, and neither metric below needs the per-partition watermark walk
        // ListTopicsAsync pays for.
        var (topicCount, partitionCount) = await new ConfluentKafkaOperations().GetTopicCountsAsync(connectionString, ct);
        return
        [
            new PluginDashboardMetric("Topics", topicCount),
            new PluginDashboardMetric("Partitions", partitionCount),
        ];
    }

    // Extracted as a pure static function so the threshold boundary is unit-testable without a
    // real broker -- same reasoning as ConfluentKafkaOperations.ComputeLag.
    internal static bool HasHighLag(ConsumerGroupSummary group) => group.TotalLag > LagProblemThreshold;

    // Short-lived cache for ListConsumerGroupsAsync's result, shared between GetDashboardProblemsAsync
    // and GetConsumerGroupBadgeAsync below. Both paths do a cluster-wide ListConsumerGroups +
    // DescribeConsumerGroups, then per group a ListConsumerGroupOffsets plus one blocking
    // QueryWatermarkOffsets per partition -- the heaviest operation this plugin performs. NavMenu.razor
    // polls GetNavBadgeAsync every 60s for every open circuit and every Kafka connection, and
    // Home.razor calls GetDashboardProblemsAsync on every Dashboard render and Wallboard refresh, so
    // without this cache the same expensive multi-round-trip broker walk runs repeatedly within the
    // same time window instead of only when a user opens the Consumer Groups page. `static` (rather
    // than an instance field) is correct here specifically because KafkaPlugin instances are
    // constructed fresh via new() on every call (see the TestConnectionAsync comment above) -- a
    // static field is the only way to share the cached result across the badge and dashboard-problems
    // call paths. The connection string is already held in memory as a plain parameter throughout
    // every method in this file, so using it as the cache key introduces no new exposure.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, IReadOnlyList<ConsumerGroupSummary> Groups)> ConsumerGroupsCache = new();

    private static readonly TimeSpan ConsumerGroupsCacheTtl = TimeSpan.FromSeconds(60);

    private static Task<IReadOnlyList<ConsumerGroupSummary>> GetCachedConsumerGroupsAsync(string connectionString, CancellationToken ct) =>
        GetCachedConsumerGroupsAsync(
            connectionString,
            DateTimeOffset.UtcNow,
            static (cs, t) => new ConfluentKafkaOperations().ListConsumerGroupsAsync(cs, t),
            ct);

    // `now` and `fetch` are parameters (rather than reading DateTimeOffset.UtcNow / constructing
    // ConfluentKafkaOperations directly) so the cache's reuse/expiry behavior is unit-testable with a
    // counting fake fetcher and a controlled clock, without a real broker -- ListConsumerGroupsAsync
    // itself can't be exercised in tests (see KafkaPluginTests' comment on GetDashboardProblemsAsync
    // for why: Confluent.Kafka's admin async API crashes the process on this environment).
    internal static async Task<IReadOnlyList<ConsumerGroupSummary>> GetCachedConsumerGroupsAsync(
        string connectionString,
        DateTimeOffset now,
        Func<string, CancellationToken, Task<IReadOnlyList<ConsumerGroupSummary>>> fetch,
        CancellationToken ct)
    {
        if (ConsumerGroupsCache.TryGetValue(connectionString, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Groups;
        }

        var groups = await fetch(connectionString, ct);
        ConsumerGroupsCache[connectionString] = (now + ConsumerGroupsCacheTtl, groups);
        return groups;
    }

    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
    {
        var groups = await GetCachedConsumerGroupsAsync(connectionString, ct);
        var problems = groups
            .Where(HasHighLag)
            .Select(g => new PluginDashboardProblem(
                "Warning",
                $"Consumer group '{g.GroupId}' has high lag",
                $"{g.TotalLag:N0} messages behind",
                BuildConsumerGroupProblemLink(connectionId, g.GroupId)))
            .ToList();

        var dlqTopics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
        problems.AddRange(dlqTopics
            .Where(HasDeadLetterMessages)
            .Select(t => new PluginDashboardProblem(
                "Warning",
                $"Dead-letter topic '{t.DlqTopicName}' has messages",
                $"{t.ApproximateMessageCount:N0} messages retained",
                "/p/kafka/dead-letter")));

        return problems;
    }

    // Extracted as a pure static function, same reasoning as HasHighLag/HasDeadLetterMessages --
    // GetDashboardProblemsAsync's own broker round trip now also includes an uncached dead-letter-
    // topics fetch (see ListDeadLetterTopicsAsync below, deliberately uncached to match
    // ServiceBusPlugin's identical GetDashboardProblemsAsync/ListQueuesAsync shape), so pre-seeding
    // only the consumer-groups cache (GetCachedConsumerGroupsAsync) no longer shields a call to the
    // full method from a real broker. This link-building expression is pulled out so its escaping/
    // connectionId behavior stays unit-testable without one.
    internal static string BuildConsumerGroupProblemLink(Guid connectionId, string groupId) =>
        $"/p/kafka/consumer-groups/{Uri.EscapeDataString(groupId)}?connectionId={connectionId}";

    // Extracted as a pure static function, same reasoning as HasHighLag above. Threshold is > 0, not
    // a large magnitude like HasHighLag's -- unlike consumer-group lag (some lag is normal under
    // load), any nonzero dead-letter count is inherently abnormal. See design spec §6.
    internal static bool HasDeadLetterMessages(DeadLetterTopicSummary topic) => topic.ApproximateMessageCount > 0;

    // Extracted as a pure static function so the "pick the minimum-timestamp topic" selection is
    // unit-testable without a real broker, same reasoning as HasHighLag/HasDeadLetterMessages.
    internal static DeadLetterTopicSummary? PickOldestDeadLetterTopic(IEnumerable<DeadLetterTopicSummary> topics) =>
        topics.Where(t => t.OldestMessageTimestamp is not null).OrderBy(t => t.OldestMessageTimestamp).FirstOrDefault();

    private const string ConsumerGroupsNavHref = "/p/kafka/consumer-groups";
    private const string DeadLetterNavHref = "/p/kafka/dead-letter";

    public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        navItemHref switch
        {
            ConsumerGroupsNavHref => GetConsumerGroupBadgeAsync(connectionString, ct),
            DeadLetterNavHref => GetDeadLetterBadgeAsync(connectionString, ct),
            _ => Task.FromResult<int?>(null),
        };

    private static async Task<int?> GetConsumerGroupBadgeAsync(string connectionString, CancellationToken ct)
    {
        var groups = await GetCachedConsumerGroupsAsync(connectionString, ct);
        var problemCount = groups.Count(HasHighLag);
        // null (never 0) when nothing is flagged -- NavMenu.razor renders a visible "0" badge for a
        // literal 0 and only omits the badge for null, confirmed against ServiceBusPlugin's
        // identical GetDeadLetterBadgeAsync pattern (design spec §6).
        return problemCount > 0 ? problemCount : null;
    }

    private static async Task<int?> GetDeadLetterBadgeAsync(string connectionString, CancellationToken ct)
    {
        var topics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
        var total = topics.Sum(t => t.ApproximateMessageCount);
        // null (never 0) when nothing is flagged -- same NavMenu.razor rendering rule
        // GetConsumerGroupBadgeAsync already follows.
        return total > 0 ? (int)total : null;
    }

    public async Task<OldestDeadLetterEntry?> GetOldestDeadLetterAsync(
        Guid connectionId, string connectionString, CancellationToken ct = default)
    {
        var topics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
        var oldest = PickOldestDeadLetterTopic(topics);

        // ResourceName/DeadLetterCount are the DLQ topic's own name and count -- matching how
        // ServiceBusPlugin's implementation reports a single queue's name and that queue's own
        // count, not an aggregate across every dead-lettered resource. See design spec §6.
        return oldest is null
            ? null
            : new OldestDeadLetterEntry(oldest.DlqTopicName, oldest.OldestMessageTimestamp!.Value, oldest.ApproximateMessageCount);
    }
}
