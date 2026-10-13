using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite;

public sealed class SqlitePlugin : IPlugin
{
    public const string PluginId = "sqlite";
    public const string OverviewHref = "/p/sqlite/overview";
    public const string TablesHref = "/p/sqlite/tables";

    // Resolved in Initialize (IPlugin.Initialize): the host constructs plugins with new(), so the
    // instance hooks below have no DI of their own. Until then they fail closed.
    private ISqliteOperations? _operations;

    public string Id => PluginId;
    public string DisplayName => "SQLite";
    public string? Monogram => "SQ";
    public string Version => "0.1.0";
    public string ConnectionKind => "sqlite";
    public string ConnectionKindDisplayName => "SQLite";

    public IReadOnlyList<PluginNavItem> NavItems { get; } =
    [
        new PluginNavItem("Overview", OverviewHref),
        new PluginNavItem("Tables", TablesHref),
    ];

    // Pages: Overview, Tables. Audited actions: integrity check, CSV export (design spec §8).
    public PluginContribution Contribution => new(PageCount: 2, ActionCount: 2);

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
}
