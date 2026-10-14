using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Access;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Access;

public class GetPermissionsQueryHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly IReadOnlyList<string> Ns = ["default"];
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly AccessCache _cache = new(new FakeTimeProvider());

    public GetPermissionsQueryHandlerTests()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");
        _ops.GetAccessAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new K8sAccessFacts("alice", [new K8sRulesReview("default", false, [])], new Dictionary<string, bool>()));
    }

    private GetPermissionsQueryHandler Sut() => new(_ops, _connections, _cache, NullLogger<GetPermissionsQueryHandler>.Instance);

    [Fact]
    public async Task Missing_connection_fails_with_not_found()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);
        var result = await Sut().HandleAsync(Id, Ns);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
    }

    [Fact]
    public async Task Probes_then_serves_from_cache()
    {
        var first = await Sut().HandleAsync(Id, Ns);
        var second = await Sut().HandleAsync(Id, Ns);

        first.Value!.Identity.Should().Be("alice");
        second.Value.Should().BeSameAs(first.Value);
        await _ops.Received(1).GetAccessAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_bypasses_cache()
    {
        await Sut().HandleAsync(Id, Ns);
        await Sut().HandleAsync(Id, Ns, refresh: true);
        await _ops.Received(2).GetAccessAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Probe_failure_returns_All_and_is_not_cached()
    {
        _ops.GetAccessAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Sut().HandleAsync(Id, Ns);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(K8sPermissions.All);
        _cache.TryGet(Id, Ns, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_is_rethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _ops.GetAccessAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Sut().HandleAsync(Id, Ns, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
