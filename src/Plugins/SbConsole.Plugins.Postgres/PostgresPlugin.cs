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
}
