using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Overview;
using SbConsole.Sdk;
using static SbConsole.Plugins.Kubernetes.Tests.TestData;

namespace SbConsole.Plugins.Kubernetes.Tests.Overview;

public class GetOverviewQueryHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _clock = new(T0);

    public GetOverviewQueryHandlerTests() =>
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private GetOverviewQueryHandler Sut() => new(_ops, _connections, _store, _clock, NullLogger<GetOverviewQueryHandler>.Instance);

    [Fact]
    public async Task Missing_connection_fails_with_not_found()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Sut().HandleAsync(Id, []);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
    }

    [Fact]
    public async Task Success_returns_fresh_view_and_stores_the_snapshot_with_its_namespaces()
    {
        var overview = OverviewWith();
        _ops.GetOverviewAsync("secret", Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "payments" })), Arg.Any<CancellationToken>())
            .Returns(overview);

        var result = await Sut().HandleAsync(Id, ["payments"]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new OverviewView(overview, false, null, null));
        var state = await OverviewSnapshotStore.ReadAsync(_store, Id, default);
        state.LastGood!.LoadedAt.Should().Be(T0);
        state.LastGood.Namespaces.Should().Equal("payments");
        state.LastGood.Overview.Should().BeEquivalentTo(overview);
    }

    [Fact]
    public async Task Failure_returns_the_last_good_overview_as_stale_with_the_error()
    {
        var good = OverviewWith();
        _ops.GetOverviewAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(good), _ => throw new HttpRequestException("refused"));
        var sut = Sut();
        await sut.HandleAsync(Id, ["b", "a"]);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await sut.HandleAsync(Id, ["a", "b"]);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Overview.Should().BeEquivalentTo(good);
        result.Value.IsStale.Should().BeTrue();
        result.Value.StaleSince.Should().Be(T0);
        result.Value.Error.Should().Be(new K8sErrorInfo("Can't reach the cluster.", null, null, true));
        var state = await OverviewSnapshotStore.ReadAsync(_store, Id, default);
        state.LastGood.Should().NotBeNull();
        state.LastFailureAt.Should().Be(T0.AddMinutes(2));
        state.LastError.Should().Be("Can't reach the cluster.");
    }

    [Fact]
    public async Task Failure_for_a_different_namespace_selection_does_not_reuse_the_snapshot()
    {
        _ops.GetOverviewAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(OverviewWith()), _ => throw new HttpRequestException("refused"));
        var sut = Sut();
        await sut.HandleAsync(Id, []);

        var result = await sut.HandleAsync(Id, ["payments"]);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Overview.Should().BeNull();
        result.Value.IsStale.Should().BeFalse();
        result.Value.StaleSince.Should().BeNull();
        result.Value.Error!.IsUnreachable.Should().BeTrue();
    }

    [Fact]
    public async Task Failure_without_snapshot_returns_null_overview_and_error()
    {
        _ops.GetOverviewAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("slow"));

        var result = await Sut().HandleAsync(Id, []);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Overview.Should().BeNull();
        result.Value.IsStale.Should().BeFalse();
        result.Value.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_and_nothing_is_recorded()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _ops.GetOverviewAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = () => Sut().HandleAsync(Id, [], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Store_write_failure_does_not_fail_the_view()
    {
        var overview = OverviewWith();
        _ops.GetOverviewAsync("secret", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(overview);
        var store = Substitute.For<IPluginStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        store.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        var sut = new GetOverviewQueryHandler(_ops, _connections, store, _clock, NullLogger<GetOverviewQueryHandler>.Instance);

        var result = await sut.HandleAsync(Id, []);

        result.Value!.Overview.Should().Be(overview);
    }
}
