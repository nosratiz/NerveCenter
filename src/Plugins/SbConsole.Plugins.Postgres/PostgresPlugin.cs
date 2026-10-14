using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres;

public sealed class PostgresPlugin : IPlugin
{
    public const string PluginId = "postgres";
    public const string Kind = "postgres";
    public const string OverviewHref = "/p/postgres/overview";
    public const string BrowseHref = "/p/postgres/browse";
    public const string QueryHref = "/p/postgres/query";
    public const string ActivityHref = "/p/postgres/activity";
    public const string MaintenanceHref = "/p/postgres/maintenance";

    // Resolved in Initialize: the host constructs plugins with new(), so instance hooks have no DI.
    // Until then they fail closed.
    private IPostgresOperations? _operations;
    private TimeProvider _clock = TimeProvider.System;

    public string Id => PluginId;
    public string DisplayName => "PostgreSQL";
    public string? Monogram => "PG";
    public string Version => "0.1.0";
    public string ConnectionKind => Kind;
    public string ConnectionKindDisplayName => "PostgreSQL";

    public Type? ConnectionFormComponentType => typeof(PostgresConnectionFields);

    public IReadOnlyList<PluginNavItem> NavItems { get; } =
    [
        new PluginNavItem("Overview", OverviewHref),
        new PluginNavItem("Browse", BrowseHref),
        new PluginNavItem("Query", QueryHref),
        new PluginNavItem("Activity", ActivityHref),
        new PluginNavItem("Maintenance", MaintenanceHref),
    ];

    public PluginContribution Contribution => new(PageCount: 5, ActionCount: 11);

    internal static ConnectionTestResult NotInitialized => new(false, "PostgreSQL plugin is not initialized.");

    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        try
        {
            var settings = PostgresConnectionSettings.Parse(secret);
            return new Dictionary<string, string>
            {
                ["Server"] = settings.Endpoint,
                ["Database"] = settings.Database,
                ["Mode"] = settings.Mode == PgAccessMode.ReadOnly ? "read-only" : "read-write",
            };
        }
        catch (FormatException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void ConfigureServices(IServiceCollection services) =>
        services.AddMarkedServices(typeof(PostgresPlugin).Assembly);

    public void Initialize(IServiceProvider services)
    {
        _operations = services.GetRequiredService<IPostgresOperations>();
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    internal void InitializeForTesting(IPostgresOperations operations, TimeProvider? clock = null)
    {
        _operations = operations;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        _operations?.TestConnectionAsync(secret, ct) ?? Task.FromResult(NotInitialized);
    public Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginDashboardMetric>>([new PluginDashboardMetric("Servers", 1)]);

    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
        _operations is null ? [] : ComputeProblems(await _operations.GetOverviewAsync(connectionString, ct));

    /// <summary>The blocked-session count on Activity; every other page, and any failure, shows no badge.</summary>
    public async Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default)
    {
        if (_operations is null || navItemHref != ActivityHref)
        {
            return null;
        }

        try
        {
            var blocked = await _operations.GetBlockedCountAsync(connectionString, ct);
            return blocked > 0 ? blocked : null;
        }
        catch (Exception)
        {
            // No logger on the plugin instance; a badge is decoration, so a broken probe just hides it.
            return null;
        }
    }

    internal static IReadOnlyList<PluginDashboardProblem> ComputeProblems(PgOverview o)
    {
        var problems = new List<PluginDashboardProblem>();
        var a = o.Attention;

        if (a.LongestBlockedFor is { } blockedFor && blockedFor > TimeSpan.FromMinutes(1))
        {
            var detail = a.TopBlockerPid is { } pid
                ? a.TopBlockerRelation is { } rel ? $"blocker pid {pid} on {rel}" : $"blocker pid {pid}"
                : "blocker unknown";
            problems.Add(new PluginDashboardProblem("Error",
                a.BlockedCount == 1 ? "1 session blocked" : $"{a.BlockedCount} sessions blocked", detail, ActivityHref));
        }

        if (a.IdleInTransaction.Count > 0)
        {
            var first = a.IdleInTransaction[0];
            var detail = $"pid {first.Pid} for {PgFormat.Duration(first.Age)}";
            if (a.IdleInTransaction.Count > 1)
            {
                detail += $" and {a.IdleInTransaction.Count - 1} more";
            }

            problems.Add(new PluginDashboardProblem("Warning", "Idle in transaction", detail, ActivityHref));
        }

        if (o.MaxConnections > 0 && (long)o.Connections * 100 / o.MaxConnections >= 90)
        {
            var pct = (long)o.Connections * 100 / o.MaxConnections;
            problems.Add(new PluginDashboardProblem("Warning", $"Connections at {pct}%",
                $"{o.Connections} / {o.MaxConnections} max_connections", OverviewHref));
        }

        if (o.Replication.MaxLag is { } lag && lag > TimeSpan.FromSeconds(30))
        {
            problems.Add(new PluginDashboardProblem("Warning", "Replication lag",
                $"{PgFormat.Duration(lag)} on {o.Replication.LaggiestName ?? "replica"}", OverviewHref));
        }

        if (o.OldestXidAge > 1_500_000_000)
        {
            problems.Add(new PluginDashboardProblem("Error", "Transaction ID wraparound",
                $"oldest xid age {PgFormat.Count(o.OldestXidAge)}", MaintenanceHref));
        }

        return problems;
    }
}
