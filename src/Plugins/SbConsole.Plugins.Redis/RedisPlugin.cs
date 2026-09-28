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

    // Pages: Overview, Keys. Streams/Pub-Sub and their mutating actions ship in later plans
    // (docs/superpowers/specs/2026-09-28-redis-plugin-design.md §1). ActionCount counts the Keys
    // browser's audited command handlers (13 mutating/destructive + the Safe field-reveal action).
    public PluginContribution Contribution => new(PageCount: 2, ActionCount: 14);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRedisOperations, RedisOperations>();
        services.AddScoped<Overview.GetOverviewQueryHandler>();
        services.AddScoped<Overview.GetMemoryByPrefixQueryHandler>();
        services.AddScoped<Overview.GetSlowLogQueryHandler>();
        services.AddScoped<Keys.KeysQueryHandler>();
        services.AddScoped<Keys.DeleteKeyCommandHandler>();
        services.AddScoped<Keys.RenameKeyCommandHandler>();
        services.AddScoped<Keys.SetTtlCommandHandler>();
        services.AddScoped<Keys.DeleteByPatternCommandHandler>();
        services.AddScoped<Keys.SetStringCommandHandler>();
        services.AddScoped<Keys.SetHashFieldCommandHandler>();
        services.AddScoped<Keys.DeleteHashFieldCommandHandler>();
        services.AddScoped<Keys.PushListCommandHandler>();
        services.AddScoped<Keys.TrimListCommandHandler>();
        services.AddScoped<Keys.AddSortedSetMemberCommandHandler>();
        services.AddScoped<Keys.IncrementSortedSetScoreCommandHandler>();
        services.AddScoped<Keys.AddSetMemberCommandHandler>();
        services.AddScoped<Keys.RemoveSetMemberCommandHandler>();
        services.AddScoped<Keys.RevealHashFieldCommandHandler>();
        // A new handler must be added here or its page fails to render at runtime.
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new RedisOperations().TestConnectionAsync(secret, ct);

    private const double MemoryProblemThresholdPercent = 90.0;

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
}
