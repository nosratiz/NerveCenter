using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SbConsole.Plugins.Kubernetes.Access;

namespace SbConsole.Plugins.Kubernetes.Tests.Access;

public class AccessCacheTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void Hit_before_ttl_miss_after()
    {
        var cache = new AccessCache(_clock);
        cache.Set(Id, ["a"], K8sPermissions.All);

        _clock.Advance(TimeSpan.FromSeconds(299));
        cache.TryGet(Id, ["a"], out var hit).Should().BeTrue();
        hit.Should().BeSameAs(K8sPermissions.All);

        _clock.Advance(TimeSpan.FromSeconds(2));
        cache.TryGet(Id, ["a"], out _).Should().BeFalse();
    }

    [Fact]
    public void Namespace_order_does_not_matter()
    {
        var cache = new AccessCache(_clock);
        cache.Set(Id, ["b", "a"], K8sPermissions.All);
        cache.TryGet(Id, ["a", "b"], out _).Should().BeTrue();
        cache.TryGet(Id, ["a"], out _).Should().BeFalse();
    }

    [Fact]
    public void Invalidate_drops_all_keys_for_the_connection_only()
    {
        var other = Guid.NewGuid();
        var cache = new AccessCache(_clock);
        cache.Set(Id, ["a"], K8sPermissions.All);
        cache.Set(Id, ["b"], K8sPermissions.All);
        cache.Set(other, ["a"], K8sPermissions.All);

        cache.Invalidate(Id);

        cache.TryGet(Id, ["a"], out _).Should().BeFalse();
        cache.TryGet(Id, ["b"], out _).Should().BeFalse();
        cache.TryGet(other, ["a"], out _).Should().BeTrue();
    }
}
