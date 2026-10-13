using Microsoft.Extensions.Logging;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Sdk;
using SbConsole.Web.Plugins;
using ConnectionInfo = SbConsole.Sdk.ConnectionInfo;

namespace SbConsole.Web.Wallboard;

public sealed record WallboardSnapshot(
    IReadOnlyList<(ConnectionInfo Connection, int DeadLetterCount)> NamespaceBacklog,
    OldestDeadLetterEntry? OldestDeadLetter,
    IReadOnlySet<Guid> UncheckedConnections,
    IReadOnlyList<ConnectionInfo> Unreachable,
    IReadOnlyList<WallboardAggregator.Bucket> Buckets1h,
    IReadOnlyList<WallboardAggregator.Bucket> Buckets12h,
    IReadOnlyList<WallboardAggregator.Bucket> Buckets24h,
    string GrowthSummary,
    long ActiveCount,
    long ActiveDeltaLastMinute,
    long DeadLetterTotal,
    long? DeadLetterDeltaLastHour,
    IReadOnlyList<DateTimeOffset> OutageEvents);

/// <summary>
/// The Azure-calling load behind the wallboard's tiles and charts, shared by the full
/// /wallboard page and the condensed dashboard widget so both read the exact same numbers
/// instead of drifting apart (see WallboardWidget.razor's summary of the same tiles).
/// </summary>
public sealed class WallboardSnapshotLoader(
    ListConnectionsQueryHandler connectionsHandler,
    PluginRegistry registry,
    IConnectionProvider connectionProvider,
    IPluginStoreFactory pluginStoreFactory,
    ListAuditEntriesQueryHandler auditHandler,
    ILogger<WallboardSnapshotLoader> logger,
    TimeProvider clock) : IScopedService
{
    public async Task<WallboardSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var connections = await connectionsHandler.HandleAsync(ct: ct);
        var unreachable = connections.Where(c => c.LastTestedAt is not null && c.LastTestSucceeded == false).ToList();

        // Every connection is checked concurrently, not one after another: a stopped broker only
        // fails after its client's connect timeout, so a sequential loop made the wallboard (and the
        // Dashboard's embedded widget) wait for the SUM of every down broker's timeout. Results are
        // merged afterwards in the original plugin/connection order.
        var checks = await Task.WhenAll(registry.Plugins.SelectMany(plugin =>
        {
            var store = pluginStoreFactory.For(plugin.Id);
            return connections
                .Where(c => c.Kind == plugin.ConnectionKind)
                .Select(connection => CheckConnectionAsync(plugin, store, connection, ct));
        }));

        var namespaceBacklog = new List<(ConnectionInfo, int)>();
        var allHistories = new List<IReadOnlyList<MetricSnapshotPoint>>();
        var historyByLabel = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>();
        var uncheckedConnections = new HashSet<Guid>();
        OldestDeadLetterEntry? oldest = null;

        foreach (var check in checks)
        {
            if (!check.Checked)
            {
                uncheckedConnections.Add(check.Connection.Id);
                continue;
            }

            if (check.DeadLettered > 0)
            {
                namespaceBacklog.Add((check.Connection, check.DeadLettered));
            }

            foreach (var (resource, history) in check.Histories)
            {
                allHistories.Add(history);
                historyByLabel[$"{check.Connection.Name} / {resource.ResourceName}"] = history;
            }

            if (check.Oldest is not null && (oldest is null || check.Oldest.EnqueuedTime < oldest.EnqueuedTime))
            {
                oldest = check.Oldest;
            }
        }

        foreach (var connection in unreachable.Where(u => namespaceBacklog.All(n => n.Item1.Id != u.Id)))
        {
            namespaceBacklog.Add((connection, 0));
        }

        var orderedBacklog = namespaceBacklog.OrderByDescending(n => n.Item2).ToList();

        var now = clock.GetUtcNow();
        var buckets1h = WallboardAggregator.BucketAndSum(allHistories, TimeSpan.FromMinutes(1), now, TimeSpan.FromHours(1));
        var buckets12h = WallboardAggregator.BucketAndSum(allHistories, TimeSpan.FromHours(1), now, TimeSpan.FromHours(12));
        var buckets24h = WallboardAggregator.BucketAndSum(allHistories, TimeSpan.FromMinutes(5), now, TimeSpan.FromHours(24));
        var growthSummary = WallboardAggregator.SummarizeGrowth(historyByLabel, TimeSpan.FromHours(12), now);

        var activeCount = buckets1h.Count > 0 ? buckets1h[^1].TotalActive : 0;
        var activeDeltaLastMinute = buckets1h.Count >= 2 ? buckets1h[^1].TotalActive - buckets1h[^2].TotalActive : 0;
        var deadLetterTotal = orderedBacklog.Sum(n => (long)n.Item2);
        var deadLetterDeltaLastHour = buckets1h.Count > 0 ? deadLetterTotal - buckets1h[0].TotalDeadLetter : (long?)null;

        // The wireframe's "outage rule": a vertical marker on the throughput chart at the moment
        // a connection.test failed, so the outage and the dip in the line read as one event. This
        // is scoped to Action: "connection.test" (not just filtered client-side after the default
        // page) so a busy audit log elsewhere can't crowd genuine test failures out of the page --
        // see AuditQuery.Action.
        var outagePage = await auditHandler.HandleAsync(
            new AuditQuery(From: now - TimeSpan.FromHours(24), To: now, Action: "connection.test", PageSize: 1000), ct);
        var outageEvents = outagePage.Entries.Where(e => !e.Succeeded).Select(e => e.At).ToList();

        return new WallboardSnapshot(
            orderedBacklog,
            oldest,
            uncheckedConnections,
            unreachable,
            buckets1h,
            buckets12h,
            buckets24h,
            growthSummary,
            activeCount,
            activeDeltaLastMinute,
            deadLetterTotal,
            deadLetterDeltaLastHour,
            outageEvents);
    }

    private sealed record ConnectionCheck(
        ConnectionInfo Connection,
        bool Checked,
        int DeadLettered,
        IReadOnlyList<(PluginResourceMetric Resource, IReadOnlyList<MetricSnapshotPoint> History)> Histories,
        OldestDeadLetterEntry? Oldest);

    private async Task<ConnectionCheck> CheckConnectionAsync(IPlugin plugin, IPluginStore store, ConnectionInfo connection, CancellationToken ct)
    {
        try
        {
            var secret = await connectionProvider.GetSecretAsync(connection.Id, ct);
            if (secret is null)
            {
                return new ConnectionCheck(connection, false, 0, [], null);
            }

            var deadLettered = 0;
            foreach (var metric in await plugin.GetDashboardMetricsAsync(secret, ct))
            {
                if (metric.Label == "Dead-lettered")
                {
                    deadLettered += metric.Count;
                }
            }

            var resources = await plugin.GetResourceMetricsAsync(secret, ct);
            var histories = await Task.WhenAll(resources.Select(async r =>
                (Resource: r, History: await MetricHistoryStore.ReadAsync(store, connection.Id, r.ResourceName))));

            var oldest = await plugin.GetOldestDeadLetterAsync(connection.Id, secret, ct);
            return new ConnectionCheck(connection, true, deadLettered, histories, oldest);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Computing wallboard data for connection {ConnectionId} failed; it may be incomplete or missing.", connection.Id);
            return new ConnectionCheck(connection, false, 0, [], null);
        }
    }
}
