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
        new PluginNavItem("Overview", "/p/redis/overview", "speedometer"),
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

    // Pages so far: Overview. No command handlers yet — this is a read-only slice; Keys/Streams/
    // Pub-Sub and their mutating actions ship in later plans (docs/superpowers/specs/
    // 2026-09-28-redis-plugin-design.md §1).
    public PluginContribution Contribution => new(PageCount: 1, ActionCount: 0);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRedisOperations, RedisOperations>();
        services.AddScoped<Overview.GetOverviewQueryHandler>();
        services.AddScoped<Overview.GetMemoryByPrefixQueryHandler>();
        services.AddScoped<Overview.GetSlowLogQueryHandler>();
        // A new handler must be added here or its page fails to render at runtime.
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new RedisOperations().TestConnectionAsync(secret, ct);
}
