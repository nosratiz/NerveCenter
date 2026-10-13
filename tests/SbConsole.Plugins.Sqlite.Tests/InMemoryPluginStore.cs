using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests;

public sealed class InMemoryPluginStore : IPluginStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.Remove(key));
}
