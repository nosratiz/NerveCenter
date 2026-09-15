using Microsoft.Extensions.Logging;
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
    long? DeadLetterDeltaLastHour);

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
    ILogger<WallboardSnapshotLoader> logger,
    TimeProvider clock)
{
    public async Task<WallboardSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var connections = await connectionsHandler.HandleAsync(ct: ct);
        var unreachable = connections.Where(c => c.LastTestedAt is not null && c.LastTestSucceeded == false).ToList();

        var namespaceBacklog = new List<(ConnectionInfo, int)>();
        var allHistories = new List<IReadOnlyList<MetricSnapshotPoint>>();
        var historyByLabel = new Dictionary<string, IReadOnlyList<MetricSnapshotPoint>>();
        var uncheckedConnections = new HashSet<Guid>();
        OldestDeadLetterEntry? oldest = null;

        foreach (var plugin in registry.Plugins)
        {
            var store = pluginStoreFactory.For(plugin.Id);
            foreach (var connection in connections.Where(c => c.Kind == plugin.ConnectionKind))
            {
                try
                {
                    var secret = await connectionProvider.GetSecretAsync(connection.Id, ct);
                    if (secret is null)
                    {
                        uncheckedConnections.Add(connection.Id);
                        continue;
                    }

                    var deadLettered = 0;
                    foreach (var metric in await plugin.GetDashboardMetricsAsync(secret, ct))
                    {
                        if (metric.Label == "Dead-lettered")
                        {
                            deadLettered += metric.Count;
                        }
                    }

                    if (deadLettered > 0)
                    {
                        namespaceBacklog.Add((connection, deadLettered));
                    }

                    var resources = await plugin.GetResourceMetricsAsync(secret, ct);
                    var histories = await Task.WhenAll(resources.Select(async r =>
                        (Resource: r, History: await MetricHistoryStore.ReadAsync(store, connection.Id, r.ResourceName))));

                    foreach (var (resource, history) in histories)
                    {
                        allHistories.Add(history);
                        historyByLabel[$"{connection.Name} / {resource.ResourceName}"] = history;
                    }

                    var connectionOldest = await plugin.GetOldestDeadLetterAsync(connection.Id, secret, ct);
                    if (connectionOldest is not null && (oldest is null || connectionOldest.EnqueuedTime < oldest.EnqueuedTime))
                    {
                        oldest = connectionOldest;
                    }
                }
                catch (Exception ex)
                {
                    uncheckedConnections.Add(connection.Id);
                    logger.LogWarning(ex, "Computing wallboard data for connection {ConnectionId} failed; it may be incomplete or missing.", connection.Id);
                }
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
            deadLetterDeltaLastHour);
    }
}
