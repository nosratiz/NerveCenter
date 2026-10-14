using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite;

public sealed class SqlitePlugin : IPlugin
{
    public const string PluginId = "sqlite";
    public const string Kind = "sqlite";
    public const string OverviewHref = "/p/sqlite/overview";
    public const string TablesHref = "/p/sqlite/tables";
    public const string QueryHref = "/p/sqlite/query";

    // No GetNavBadgeAsync for Backups (spec §11.7 wanted "hours since the last backup"): the SDK
    // hands that hook only (href, secret) -- no connection id or IPluginStore -- so backup history
    // can't be read there. The dashboard's "Backup overdue" problem carries that signal instead.
    public const string BackupsHref = "/p/sqlite/backups";

    // Resolved in Initialize (IPlugin.Initialize): the host constructs plugins with new(), so the
    // instance hooks below have no DI of their own. Until then they fail closed.
    private ISqliteOperations? _operations;
    private TimeProvider _clock = TimeProvider.System;

    public string Id => PluginId;
    public string DisplayName => "SQLite";
    public string? Monogram => "SQ";
    public string Version => "0.1.0";
    public string ConnectionKind => Kind;
    public string ConnectionKindDisplayName => "SQLite";
    public Type? ConnectionFormComponentType => typeof(SqliteConnectionFields);

    public IReadOnlyList<PluginNavItem> NavItems { get; } =
    [
        new PluginNavItem("Overview", OverviewHref),
        new PluginNavItem("Tables", TablesHref),
        new PluginNavItem("Query", QueryHref),
        new PluginNavItem("Backups", BackupsHref),
    ];

    // Pages: Overview, Tables, Query, Backups (§11.9). Audited actions (§8, §11): integrity check,
    // table CSV export, query CSV export, write script, WAL checkpoint, backup create/verify/policy/
    // prune/download/restore, table create/rename/drop, REINDEX, recover, new database (§13).
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 17);

    internal static ConnectionTestResult NotInitialized => new(false, "SQLite plugin is not initialized.");

    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        try
        {
            var settings = SqliteConnectionSettings.Parse(secret);
            return new Dictionary<string, string>
            {
                // Basename only: the directory layout of the host is not something to print on every card.
                ["File"] = System.IO.Path.GetFileName(settings.Path),
                ["Mode"] = settings.Mode == SqliteAccessMode.ReadOnly ? "read-only" : "read-write",
            };
        }
        catch (FormatException)
        {
            return new Dictionary<string, string>();
        }
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddMarkedServices(typeof(SqlitePlugin).Assembly);
        // Scheduled backups + growth samples (spec §11.7, §11.8); hosted services can't use the markers.
        services.AddHostedService<SqliteBackupScheduler>();
    }

    public void Initialize(IServiceProvider services)
    {
        _operations = services.GetRequiredService<ISqliteOperations>();
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    internal void InitializeForTesting(ISqliteOperations operations, TimeProvider? clock = null)
    {
        _operations = operations;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        _operations?.TestConnectionAsync(secret, ct) ?? Task.FromResult(NotInitialized);

    internal const double FreePagesProblemPercent = 20.0;

    // One per connection, so the host's same-label summing reads as "N databases" (design spec §8).
    public Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginDashboardMetric>>([new PluginDashboardMetric("Databases", 1)]);

    public Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
        _operations is null
            ? Task.FromResult<IReadOnlyList<PluginDashboardProblem>>([])
            : ComputeProblemsAsync(_operations, connectionId, connectionString, store, _clock.GetUtcNow(), ct);

    /// <summary>
    /// Error when the last stored integrity check failed; Warning for FK violations it found, for a
    /// newest backup record that failed, for an enabled backup policy whose newest success is older
    /// than BackupSchedule.OverdueHours (or missing), and for a file that's at least
    /// FreePagesProblemPercent free pages. An unreadable file throws -- the host already reports
    /// unreachable connections on its own.
    /// </summary>
    internal static async Task<IReadOnlyList<PluginDashboardProblem>> ComputeProblemsAsync(
        ISqliteOperations operations, Guid connectionId, string secret, IPluginStore store, DateTimeOffset now, CancellationToken ct)
    {
        var problems = new List<PluginDashboardProblem>();
        var integrity = await IntegrityResultStore.ReadAsync(store, connectionId, ct);
        if (integrity is { IntegrityOk: false })
        {
            var n = integrity.Messages.Count;
            problems.Add(new PluginDashboardProblem("Error", "Integrity check failed",
                $"{n} {(n == 1 ? "problem" : "problems")} found by the check started {integrity.RanAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC",
                OverviewHref));
        }

        if (integrity is { ForeignKeyViolations: > 0 })
        {
            var v = integrity.ForeignKeyViolations;
            problems.Add(new PluginDashboardProblem("Warning", "Foreign key violations",
                v == 1 ? "1 row violates a foreign key" : $"{SqliteFormat.Count(v)} rows violate a foreign key", OverviewHref));
        }

        var history = await BackupHistoryStore.ReadAsync(store, connectionId, ct);
        if (history.MaxBy(r => r.TakenAt) is { Succeeded: false } failed)
        {
            problems.Add(new PluginDashboardProblem("Warning", "Last backup failed",
                $"{failed.Error ?? "Unknown error"} ({failed.TakenAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC)", BackupsHref));
        }

        var policy = await BackupPolicyStore.ReadAsync(store, connectionId, ct);
        if (BackupSchedule.IsOverdue(policy, history, now))
        {
            var hours = BackupSchedule.HoursSinceLastSuccess(history, now);
            problems.Add(new PluginDashboardProblem("Warning", "Backup overdue",
                hours is { } h ? $"Last successful backup {h.ToString("0", CultureInfo.InvariantCulture)} h ago" : "No successful backup yet",
                BackupsHref));
        }

        var overview = await operations.GetOverviewAsync(secret, ct);
        if (overview.PageCount > 0 && overview.FreePercent >= FreePagesProblemPercent)
        {
            problems.Add(new PluginDashboardProblem("Warning",
                $"{overview.FreePercent.ToString("0", CultureInfo.InvariantCulture)}% free pages",
                $"VACUUM would reclaim {SqliteFormat.Bytes(overview.FreeBytes)}", OverviewHref));
        }

        return problems;
    }
}
