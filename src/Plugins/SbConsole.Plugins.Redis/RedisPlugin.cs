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

    // A custom connection form (mockup 1f) ships later in this same plan; until then the host
    // falls back to its default flat-textbox secret editor (SDK default behaviour).
    public Type? ConnectionFormComponentType => null;

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
        catch (FormatException)
        {
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
        // A new handler must be added here or its page fails to render at runtime.
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new RedisOperations().TestConnectionAsync(secret, ct);
}
