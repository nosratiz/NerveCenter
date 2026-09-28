using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// Every method takes the raw connection secret as its first parameter — one shared instance
/// serves every connection (docs/design.md §6.8). Grows with each later task/plan; see
/// docs/superpowers/specs/2026-09-28-redis-plugin-design.md §4 for the full target shape.
/// </summary>
public interface IRedisOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
}
