using System.Collections.Concurrent;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Access;

/// <summary>5-minute cache of probed permissions, per connection and (order-insensitive) namespace set.</summary>
public sealed class AccessCache(TimeProvider clock) : ISingletonService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<(Guid Id, string Key), (K8sPermissions Permissions, DateTimeOffset At)> _entries = new();

    private static string KeyOf(IReadOnlyList<string> namespaces) =>
        string.Join(",", namespaces.OrderBy(n => n, StringComparer.Ordinal));

    public bool TryGet(Guid connectionId, IReadOnlyList<string> namespaces, out K8sPermissions permissions)
    {
        var key = (connectionId, KeyOf(namespaces));
        if (_entries.TryGetValue(key, out var entry))
        {
            if (clock.GetUtcNow() - entry.At < Ttl)
            {
                permissions = entry.Permissions;
                return true;
            }

            _entries.TryRemove(key, out _);
        }

        permissions = null!;
        return false;
    }

    public void Set(Guid connectionId, IReadOnlyList<string> namespaces, K8sPermissions permissions) =>
        _entries[(connectionId, KeyOf(namespaces))] = (permissions, clock.GetUtcNow());

    public void Invalidate(Guid connectionId)
    {
        foreach (var key in _entries.Keys.Where(k => k.Id == connectionId))
        {
            _entries.TryRemove(key, out _);
        }
    }
}
