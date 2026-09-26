using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Routing;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq;

public sealed class RabbitMqPlugin : IPlugin
{
    public string Id => "rabbitmq";
    public string DisplayName => "RabbitMQ";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Overview", "/p/rabbitmq/overview"),
        new("Exchanges", "/p/rabbitmq/exchanges"),
        new("Queues", "/p/rabbitmq/queues"),
        new("Shovels & policies", "/p/rabbitmq/shovels"),
    ];
    public string ConnectionKind => "rabbitmq";
    public string ConnectionKindDisplayName => "RabbitMQ";

    public Type? ConnectionFormComponentType => typeof(RabbitConnectionFields);

    // Persisted in plaintext (Connection.SummaryJson): host and vhost only, never credentials.
    // Parse skips malformed segments, so this never throws.
    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        var parsed = RabbitConfigParser.Parse(secret);
        var host = parsed.GetValueOrDefault("host", "");
        var vhost = parsed.GetValueOrDefault("vhost", "");
        return new Dictionary<string, string>
        {
            ["Host"] = host.Length > 0 ? host : "?",
            ["Vhost"] = vhost.Length > 0 ? vhost : "/",
        };
    }

    // Pages: Overview, Exchanges, Queues, QueueDetail, GetMessages, ShovelsPolicies (6).
    // Actions (one per command handler): Create/Delete exchange, Create/Delete/Purge queue,
    // Add/Remove binding, Create/Delete/Restart shovel, Publish, Republish (also backs the Get
    // messages page's Requeue), Consume (13). Dialogs are not counted as pages.
    public PluginContribution Contribution => new(PageCount: 6, ActionCount: 13);

    // RabbitOperations is stateless, so one instance serves every connection. Then one scoped
    // registration per handler class; pages resolve handlers by concrete type (no MediatR). A new
    // handler must be added here or its page fails to render at runtime.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRabbitOperations, RabbitOperations>();
        services.AddScoped<Connections.GetConnectionEchoQueryHandler>();
        services.AddScoped<Connections.ListVhostsQueryHandler>();
        services.AddScoped<Overview.GetOverviewQueryHandler>();
        services.AddScoped<Exchanges.ListExchangesQueryHandler>();
        services.AddScoped<Exchanges.ListExchangeBindingsQueryHandler>();
        services.AddScoped<Exchanges.CreateExchangeCommandHandler>();
        services.AddScoped<Exchanges.DeleteExchangeCommandHandler>();
        services.AddScoped<Queues.ListQueuesQueryHandler>();
        services.AddScoped<Queues.GetQueueDetailQueryHandler>();
        services.AddScoped<Queues.CreateQueueCommandHandler>();
        services.AddScoped<Queues.DeleteQueueCommandHandler>();
        services.AddScoped<Queues.PurgeQueueCommandHandler>();
        services.AddScoped<Bindings.AddBindingCommandHandler>();
        services.AddScoped<Bindings.RemoveBindingCommandHandler>();
        services.AddScoped<Messages.PeekMessagesQueryHandler>();
        services.AddScoped<Messages.ConsumeMessagesCommandHandler>();
        services.AddScoped<Messages.PublishMessageCommandHandler>();
        services.AddScoped<Messages.RepublishMessagesCommandHandler>();
        services.AddScoped<Shovels.ListShovelsQueryHandler>();
        services.AddScoped<Shovels.CreateShovelCommandHandler>();
        services.AddScoped<Shovels.DeleteShovelCommandHandler>();
        services.AddScoped<Shovels.RestartShovelCommandHandler>();
        services.AddScoped<Policies.ListPoliciesQueryHandler>();
    }

    // The host calls this on a new()'d plugin, outside DI (AwsPlugin precedent), so it builds its
    // own RabbitOperations. The split AMQP/management result never throws for a bad secret or an
    // unreachable broker -- both come back as a failed ConnectionTestResult.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new RabbitOperations().TestConnectionAsync(secret, ct);

    // --- host hooks (design spec §9) ---

    /// <summary>
    /// Everything the badge/dashboard/resource-metric hooks derive from: every node, every queue
    /// across every vhost, and each vhost's dead-letter topology.
    /// </summary>
    internal sealed record BrokerSnapshot(
        IReadOnlyList<NodeSummary> Nodes,
        IReadOnlyList<QueueSummary> Queues,
        IReadOnlyDictionary<string, DeadLetterTopology> TopologyByVhost);

    // Vhost counts are small, but a broker with many tenants shouldn't get one burst of 2N requests.
    private const int MaxConcurrentVhostFetches = 4;

    // Nodes and all-vhost queues concurrently, then bindings + exchanges per vhost that has queues
    // (BindingInfo/ExchangeSummary carry no vhost, so the all-vhost /api/bindings listing can't be
    // split back per vhost -- hence one /api/bindings/{vhost} call each). Failures propagate.
    internal static async Task<BrokerSnapshot> FetchSnapshotAsync(IRabbitOperations ops, string secret, CancellationToken ct)
    {
        var nodesTask = ops.ListNodesAsync(secret, ct);
        var queuesTask = ops.ListQueuesAsync(secret, null, ct);
        await Task.WhenAll(nodesTask, queuesTask);
        var nodes = await nodesTask;
        var queues = await queuesTask;

        var vhosts = queues.Select(q => q.Vhost).Distinct(StringComparer.Ordinal).ToList();
        using var throttle = new SemaphoreSlim(MaxConcurrentVhostFetches);
        var perVhost = await Task.WhenAll(vhosts.Select(async vhost =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var bindingsTask = ops.ListBindingsAsync(secret, vhost, ct);
                var exchangesTask = ops.ListExchangesAsync(secret, vhost, ct);
                await Task.WhenAll(bindingsTask, exchangesTask);
                return (Vhost: vhost, Bindings: await bindingsTask, Exchanges: await exchangesTask);
            }
            finally
            {
                throttle.Release();
            }
        }));

        var topology = DeadLetterTopology.ComputePerVhost(
            queues,
            perVhost.ToDictionary(v => v.Vhost, v => v.Bindings, StringComparer.Ordinal),
            perVhost.ToDictionary(v => v.Vhost, v => v.Exchanges, StringComparer.Ordinal));
        return new BrokerSnapshot(nodes, queues, topology);
    }

    // Same shape and reasoning as AwsPlugin.GetCachedQueuesAsync: NavMenu polls the badges every
    // 60 s per circuit, Home/Wallboard call the dashboard hooks on every render, and the metrics
    // collector calls GetResourceMetricsAsync periodically -- all over identical data. `static`
    // because plugins are new()'d per call. Keyed by the secret, which is already held in memory
    // throughout. A failed fetch throws before the cache is written, so failures are never cached.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, BrokerSnapshot Snapshot)> SnapshotCache = new();

    private static readonly TimeSpan SnapshotCacheTtl = TimeSpan.FromSeconds(60);

    private static Task<BrokerSnapshot> GetCachedSnapshotAsync(string secret, CancellationToken ct) =>
        GetCachedSnapshotAsync(
            secret,
            DateTimeOffset.UtcNow,
            static (s, t) => FetchSnapshotAsync(new RabbitOperations(), s, t),
            ct);

    // `now` and `fetch` are parameters so reuse/expiry is unit-testable without a broker.
    internal static async Task<BrokerSnapshot> GetCachedSnapshotAsync(
        string secret,
        DateTimeOffset now,
        Func<string, CancellationToken, Task<BrokerSnapshot>> fetch,
        CancellationToken ct)
    {
        if (SnapshotCache.TryGetValue(secret, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Snapshot;
        }

        var snapshot = await fetch(secret, ct);
        SnapshotCache[secret] = (now + SnapshotCacheTtl, snapshot);
        return snapshot;
    }

    private const string OverviewNavHref = "/p/rabbitmq/overview";
    private const string QueuesNavHref = "/p/rabbitmq/queues";

    // DLQ-ness is per vhost: DeadLetterTopology.Compute only sees one vhost's queues/bindings.
    internal static bool IsDeadLetterQueue(BrokerSnapshot snapshot, QueueSummary queue) =>
        snapshot.TopologyByVhost.TryGetValue(queue.Vhost, out var topology) && topology.IsDeadLetterQueue(queue.Name);

    private static long DeadLetteredTotal(BrokerSnapshot snapshot)
    {
        // Saturating sum: Ready is a long and the result is clamped to int anyway.
        long total = 0;
        foreach (var q in snapshot.Queues.Where(q => IsDeadLetterQueue(snapshot, q)))
        {
            total = q.Ready > long.MaxValue - total ? long.MaxValue : total + q.Ready;
        }

        return total;
    }

    private static int ClampToInt(long value) => (int)Math.Min(value, int.MaxValue);

    private static bool InAlarm(NodeSummary node) => node.MemAlarm || node.DiskAlarm;

    // null (never 0) when there's nothing to report -- NavMenu renders a visible "0" for a literal 0.
    internal static int? ComputeNavBadge(string navItemHref, BrokerSnapshot snapshot)
    {
        long value = navItemHref switch
        {
            OverviewNavHref => snapshot.Nodes.Count(InAlarm),
            QueuesNavHref => DeadLetteredTotal(snapshot),
            _ => 0,
        };
        return value > 0 ? ClampToInt(value) : null;
    }

    internal static IReadOnlyList<PluginDashboardMetric> BuildDashboardMetrics(BrokerSnapshot snapshot) =>
    [
        new PluginDashboardMetric("Queues", snapshot.Queues.Count),
        // "Dead-lettered" is the label WallboardSnapshotLoader sums into its per-connection backlog.
        new PluginDashboardMetric("Dead-lettered", ClampToInt(DeadLetteredTotal(snapshot))),
    ];

    // "{vhost}/{name}" always (so the default vhost gives "//orders"): the name is the metric
    // history key, so it must be unambiguous across vhosts and stable -- unlike the problem title
    // below, it is never shortened for "/".
    internal static IReadOnlyList<PluginResourceMetric> BuildResourceMetrics(BrokerSnapshot snapshot) =>
        snapshot.Queues
            .Select(q => new PluginResourceMetric($"{q.Vhost}/{q.Name}", q.Ready, IsDeadLetterQueue(snapshot, q) ? q.Ready : 0))
            .ToList();

    // Order: node alarms (Errors), then queues in listing order. A DLQ with a backlog is reported
    // only as dead-lettered -- DLQs normally have no consumers, so "no consumers" would be noise.
    internal static IReadOnlyList<PluginDashboardProblem> BuildDashboardProblems(Guid connectionId, BrokerSnapshot snapshot)
    {
        var problems = new List<PluginDashboardProblem>();
        var overviewLink = $"{OverviewNavHref}?connectionId={connectionId}";
        foreach (var node in snapshot.Nodes)
        {
            if (node.MemAlarm)
            {
                problems.Add(new PluginDashboardProblem("Error", node.Name, "Memory alarm — publishers blocked", overviewLink));
            }

            if (node.DiskAlarm)
            {
                problems.Add(new PluginDashboardProblem("Error", node.Name, "Disk alarm — publishers blocked", overviewLink));
            }
        }

        foreach (var q in snapshot.Queues.Where(q => q.Ready > 0))
        {
            string detail;
            if (IsDeadLetterQueue(snapshot, q))
            {
                detail = string.Create(CultureInfo.InvariantCulture, $"{q.Ready:N0} dead-lettered");
            }
            else if (q.Consumers == 0)
            {
                detail = string.Create(CultureInfo.InvariantCulture, $"{q.Ready:N0} ready, no consumers");
            }
            else
            {
                continue;
            }

            var title = q.Vhost == "/" ? q.Name : $"{q.Vhost}/{q.Name}";
            problems.Add(new PluginDashboardProblem("Warning", title, detail, BuildQueueDetailLink(connectionId, q)));
        }

        return problems;
    }

    // Carries connectionId and vhost: QueueDetail resolves both from the query string.
    internal static string BuildQueueDetailLink(Guid connectionId, QueueSummary queue) =>
        $"{QueuesNavHref}/{Uri.EscapeDataString(queue.Name)}?connectionId={connectionId}&vhost={Uri.EscapeDataString(queue.Vhost)}";

    // Failures (unreachable broker, 401 from the management API) deliberately propagate, like
    // AwsPlugin's hooks: every host call site catches and logs, and Home/Wallboard mark the
    // connection "unchecked" -- swallowing here would show a broken connection as healthy.
    public async Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        navItemHref is OverviewNavHref or QueuesNavHref
            ? ComputeNavBadge(navItemHref, await GetCachedSnapshotAsync(connectionString, ct))
            : null;

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default) =>
        BuildDashboardMetrics(await GetCachedSnapshotAsync(connectionString, ct));

    public async Task<IReadOnlyList<PluginResourceMetric>> GetResourceMetricsAsync(string connectionString, CancellationToken ct = default) =>
        BuildResourceMetrics(await GetCachedSnapshotAsync(connectionString, ct));

    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
        BuildDashboardProblems(connectionId, await GetCachedSnapshotAsync(connectionString, ct));

    // GetOldestDeadLetterAsync is deliberately NOT implemented (SDK default: null). The management
    // API exposes no per-message age, and reading one needs an AMQP basic.get, which has side
    // effects (it redelivers the message, bumping its redelivered flag / delivery count, and on a
    // quorum queue counts toward the delivery limit) -- unacceptable for a passive wallboard poll.
    // QueueSummary.HeadMessageTimestamp is only the publisher-set timestamp property, often absent.
}
