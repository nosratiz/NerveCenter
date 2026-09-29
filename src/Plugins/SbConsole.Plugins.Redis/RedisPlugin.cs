using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis;

public sealed class RedisPlugin : IPlugin
{
    public string Id => "redis";
    public string DisplayName => "Redis";
    public string Version => "0.1.0";
    public string ConnectionKind => "redis";
    public string ConnectionKindDisplayName => "Redis";

    public IReadOnlyList<PluginNavItem> NavItems { get; } =
    [
        new PluginNavItem("Overview", "/p/redis/overview"),
        new PluginNavItem("Keys", "/p/redis/keys"),
        new PluginNavItem("Streams", StreamsNavHref),
        new PluginNavItem("Pub/Sub", "/p/redis/pubsub"),
    ];

    public Type? ConnectionFormComponentType => typeof(RedisConnectionFields);

    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        try
        {
            var settings = RedisConnectionSettings.Parse(secret);
            return new Dictionary<string, string>
            {
                ["Host"] = settings.EndpointHostPorts.FirstOrDefault() ?? "?",
                ["Topology"] = settings.Topology.ToString(),
            };
        }
        catch (Exception)
        {
            // RedisConnectionSettings.Parse can throw FormatException (wrong '|' count) or
            // ArgumentException (bad topology token, or a ConfigurationOptions keyword it
            // rejects) — this method's contract is "never throw", so both are swallowed the
            // same way: an unparsable secret just gets no summary chips.
            return new Dictionary<string, string>();
        }
    }

    // Pages: Overview, Keys, Streams, Pub/Sub. ActionCount counts the Keys browser's 14 audited
    // command handlers (13 mutating/destructive + the Safe field-reveal action) plus the Streams
    // group's 6 (add/trim/ack/claim/delete/delete-consumer) plus phase 3's 3 (FLUSHDB, Pub/Sub subscribe-start,
    // Pub/Sub publish) -- 23 total. AclQueryHandler/PubSubQueryHandler's channel listing are
    // read-only queries and don't count.
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 23);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddMarkedServices(typeof(RedisPlugin).Assembly);
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new RedisOperations().TestConnectionAsync(secret, ct);

    internal const double MemoryProblemThresholdPercent = 90.0;

    // The host's dashboard (Home.razor) sums same-label PluginDashboardMetric values across every
    // connection of a plugin -- correct for a count, meaningless for a percentage (two Redis
    // connections at 60% and 90% memory would render as "150"). The real memory-pressure signal is
    // already surfaced correctly, per-connection, via GetDashboardProblemsAsync below, which isn't
    // affected by summing since problems are listed individually rather than aggregated. So this
    // hook reports nothing for now, rather than inventing a new summable metric (e.g. a
    // connections-over-threshold count) without an actual dashboard consumer for it.
    public Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(
        string connectionString, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginDashboardMetric>>([]);

    public Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
        GetDashboardProblemsAsyncForTesting(new RedisOperations(), connectionId, connectionString, ct);

    internal async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsyncForTesting(
        IRedisOperations operations, Guid connectionId, string connectionString, CancellationToken ct = default)
    {
        var overview = await operations.GetOverviewAsync(connectionString, ct);
        if (overview.MaxMemoryBytes is not { } max || max <= 0)
        {
            return [];
        }

        var usedPercent = 100.0 * overview.UsedMemoryBytes / max;
        if (usedPercent < MemoryProblemThresholdPercent)
        {
            return [];
        }

        return
        [
            new PluginDashboardProblem(
                "Warning",
                "Memory usage is approaching the eviction threshold",
                $"{usedPercent:N0}% of maxmemory ({overview.MaxMemoryPolicy})",
                "/p/redis/overview"),
        ];
    }

    // ---- Streams nav badge / oldest-dead-letter (design spec §2/§8) ----
    //
    // GetOldestDeadLetterAsync: RabbitMQ leaves this at the SDK default because peeking a queue
    // has a side effect (basic.get redelivers/bumps delivery count). Redis's XPENDING has none, and
    // the pending-entries list is exactly the "dead-letter" analogue the hook exists for, so Redis
    // implements it here -- using the oldest pending entry's idle time (per stream+group, the
    // lowest-id/first-added pending entry) to derive an effective enqueued-at timestamp
    // (now - idle), same as the design decision states. The "pel" nav badge on the Streams item is
    // the same computation's total pending count, so both hooks share one fetch.
    //
    // Both are scanned against the connection's own DefaultDatabase (from RedisConnectionSettings,
    // the same field ConnectionMultiplexer.GetDatabase() would default to) rather than every
    // database 0-15 -- Redis has no single cross-database call the way RabbitMQ's management API
    // covers every vhost in one request, and scanning all 16 on every dashboard/nav poll was judged
    // too expensive; this is a deliberate scope narrowing, matching how phase 1's Overview page is
    // already server-wide rather than per-database. Revisit if a real multi-database Streams use
    // case shows up.
    //
    // The underlying scan is a SCAN TYPE stream loop plus one XINFO GROUPS and (for every group with
    // pending entries) one XPENDING call per stream -- exactly the "listing every stream via SCAN
    // plus XPENDING per stream" cost flagged as a concern for a per-render nav badge. So, like
    // RabbitMqPlugin.GetCachedSnapshotAsync (docs/design.md §6.8), the result is cached for 60s per
    // secret (NavMenu polls every connection's badges on that interval) rather than re-scanned on
    // every call; a fetch that faults or is cancelled is never cached, so failures always retry.

    internal sealed record StreamPendingOverview(long TotalPendingCount, OldestDeadLetterEntry? Oldest);

    private const string StreamsNavHref = "/p/redis/streams";
    private const int MaxStreamPendingScanSteps = 50; // same cap as Keys.razor's MaxSteps -- a safety bound, not an expected ceiling.
    private const int StreamPendingScanCount = 1000;

    public async Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default)
    {
        if (navItemHref != StreamsNavHref)
        {
            return null;
        }

        return ComputeStreamsBadge(await GetCachedStreamPendingOverviewAsync(connectionString, ct));
    }

    // null (never 0) when nothing is pending -- NavMenu.razor renders a visible "0" badge for a
    // literal 0 and only omits the badge for null, matching Kafka/RabbitMQ's identical rule.
    // Extracted as a pure function so this arithmetic (including the int clamp) is unit-testable
    // without going through the cache/fetch plumbing, same reasoning as Kafka's
    // GetConsumerGroupBadgeAsync/PickOldestDeadLetterTopic split.
    internal static int? ComputeStreamsBadge(StreamPendingOverview overview) =>
        overview.TotalPendingCount > 0 ? (int)Math.Min(overview.TotalPendingCount, int.MaxValue) : null;

    public async Task<OldestDeadLetterEntry?> GetOldestDeadLetterAsync(
        Guid connectionId, string connectionString, CancellationToken ct = default) =>
        (await GetCachedStreamPendingOverviewAsync(connectionString, ct)).Oldest;

    // `now` and `fetch` are parameters so reuse/expiry/single-flight are unit-testable without a
    // server, same shape as RabbitMqPlugin.GetCachedSnapshotAsync.
    internal static async Task<StreamPendingOverview> FetchStreamPendingOverviewAsync(
        IRedisOperations operations, string secret, CancellationToken ct)
    {
        var database = RedisConnectionSettings.Parse(secret).DefaultDatabase;
        var now = DateTimeOffset.UtcNow;

        long totalPending = 0;
        OldestDeadLetterEntry? oldest = null;
        DateTimeOffset? oldestEnqueuedTime = null;

        long cursor = 0;
        var steps = 0;
        do
        {
            var page = await operations.ScanStreamsAsync(secret, new ScanStreamsRequest(database, null, cursor, StreamPendingScanCount), ct);
            cursor = page.Cursor;
            steps++;

            foreach (var stream in page.Matches)
            {
                var groups = await operations.GetStreamConsumerGroupsAsync(secret, database, stream.Key, ct);
                foreach (var group in groups.Where(g => g.PendingCount > 0))
                {
                    totalPending += group.PendingCount;

                    // The single oldest (lowest-id) pending entry for this group -- see the
                    // decision note above for why idle time, not the entry's own id, becomes the
                    // effective enqueued-at timestamp.
                    var oldestInGroup = await operations.GetStreamPendingEntriesAsync(secret, database, stream.Key, group.Name, 1, ct);
                    if (oldestInGroup.Count == 0)
                    {
                        continue;
                    }

                    var enqueuedTime = now - oldestInGroup[0].Idle;
                    if (oldestEnqueuedTime is null || enqueuedTime < oldestEnqueuedTime)
                    {
                        oldestEnqueuedTime = enqueuedTime;
                        oldest = new OldestDeadLetterEntry($"{stream.Key}/{group.Name}", enqueuedTime, group.PendingCount);
                    }
                }
            }
        } while (cursor != 0 && steps < MaxStreamPendingScanSteps);

        return new StreamPendingOverview(totalPending, oldest);
    }

    private sealed class StreamPendingCacheEntry(DateTimeOffset expiresAt, Lazy<Task<StreamPendingOverview>> overview)
    {
        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public Lazy<Task<StreamPendingOverview>> Overview { get; } = overview;
    }

    private static readonly ConcurrentDictionary<string, StreamPendingCacheEntry> StreamPendingCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan StreamPendingCacheTtl = TimeSpan.FromSeconds(60);

    internal static string StreamPendingCacheKey(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    internal static IReadOnlyCollection<string> StreamPendingCacheKeys => StreamPendingCache.Keys.ToList();

    private static Task<StreamPendingOverview> GetCachedStreamPendingOverviewAsync(string secret, CancellationToken ct) =>
        GetCachedStreamPendingOverviewAsync(
            secret,
            DateTimeOffset.UtcNow,
            static (s, t) => FetchStreamPendingOverviewAsync(new RedisOperations(), s, t),
            ct);

    internal static Task<StreamPendingOverview> GetCachedStreamPendingOverviewAsync(
        string secret,
        DateTimeOffset now,
        Func<string, CancellationToken, Task<StreamPendingOverview>> fetch,
        CancellationToken ct)
    {
        var key = StreamPendingCacheKey(secret);
        while (true)
        {
            if (StreamPendingCache.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
            {
                return existing.Overview.Value.WaitAsync(ct);
            }

            var candidate = new StreamPendingCacheEntry(
                now + StreamPendingCacheTtl,
                new Lazy<Task<StreamPendingOverview>>(() => RunStreamPendingFetchAsync(fetch, secret)));
            var stored = existing is null ? StreamPendingCache.TryAdd(key, candidate) : StreamPendingCache.TryUpdate(key, candidate, existing);
            if (!stored)
            {
                continue; // Another caller won the race; use its entry.
            }

            SweepExpiredStreamPendingCache(now);
            var task = candidate.Overview.Value;
            _ = task.ContinueWith(
                _ => StreamPendingCache.TryRemove(new KeyValuePair<string, StreamPendingCacheEntry>(key, candidate)),
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task.WaitAsync(ct);
        }
    }

    // async so a fetch that throws synchronously still becomes a faulted Task (never a cached throw in the Lazy).
    private static async Task<StreamPendingOverview> RunStreamPendingFetchAsync(
        Func<string, CancellationToken, Task<StreamPendingOverview>> fetch, string secret) =>
        await fetch(secret, CancellationToken.None);

    private static void SweepExpiredStreamPendingCache(DateTimeOffset now)
    {
        foreach (var entry in StreamPendingCache)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                StreamPendingCache.TryRemove(entry);
            }
        }
    }
}
