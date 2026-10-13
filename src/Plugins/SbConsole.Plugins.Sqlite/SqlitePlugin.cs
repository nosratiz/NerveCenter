using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite;

public sealed class SqlitePlugin : IPlugin
{
    public const string PluginId = "sqlite";
    public const string OverviewHref = "/p/sqlite/overview";
    public const string TablesHref = "/p/sqlite/tables";
    public const string QueryHref = "/p/sqlite/query";

    // Resolved in Initialize (IPlugin.Initialize): the host constructs plugins with new(), so the
    // instance hooks below have no DI of their own. Until then they fail closed.
    private ISqliteOperations? _operations;

    public string Id => PluginId;
    public string DisplayName => "SQLite";
    public string? Monogram => "SQ";
    public string Version => "0.1.0";
    public string ConnectionKind => "sqlite";
    public string ConnectionKindDisplayName => "SQLite";
    public Type? ConnectionFormComponentType => typeof(SqliteConnectionFields);

    public IReadOnlyList<PluginNavItem> NavItems { get; } =
    [
        new PluginNavItem("Overview", OverviewHref),
        new PluginNavItem("Tables", TablesHref),
        new PluginNavItem("Query", QueryHref),
    ];

    // Pages: Overview, Tables, Query. Audited actions: integrity check, CSV export (design spec §8);
    // the phase 2/3 action count is settled with §11.9's contribution update.
    public PluginContribution Contribution => new(PageCount: 3, ActionCount: 2);

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

    public void ConfigureServices(IServiceCollection services) =>
        services.AddMarkedServices(typeof(SqlitePlugin).Assembly);

    public void Initialize(IServiceProvider services) =>
        _operations = services.GetRequiredService<ISqliteOperations>();

    internal void InitializeForTesting(ISqliteOperations operations) => _operations = operations;

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
            : ComputeProblemsAsync(_operations, connectionId, connectionString, store, ct);

    /// <summary>
    /// Error when the last stored integrity check failed; Warning for FK violations it found, and
    /// for a file that's at least FreePagesProblemPercent free pages. An unreadable file throws --
    /// the host already reports unreachable connections on its own.
    /// </summary>
    internal static async Task<IReadOnlyList<PluginDashboardProblem>> ComputeProblemsAsync(
        ISqliteOperations operations, Guid connectionId, string secret, IPluginStore store, CancellationToken ct)
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
