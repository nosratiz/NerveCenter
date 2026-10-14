using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Overview;

public class GetOverviewQueryHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _clock = new(OverviewTestData.T0);

    public GetOverviewQueryHandlerTests() =>
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private GetOverviewQueryHandler Sut() => new(_ops, _connections, _store, _clock, NullLogger<GetOverviewQueryHandler>.Instance);

    [Fact]
    public async Task Missing_connection_fails_with_not_found()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Sut().HandleAsync(Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
    }

    [Fact]
    public async Task Success_returns_fresh_view_and_writes_snapshot()
    {
        var overview = OverviewTestData.Overview();
        _ops.GetOverviewAsync("secret", Arg.Any<CancellationToken>()).Returns(overview);

        var result = await Sut().HandleAsync(Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Overview.Should().Be(overview);
        result.Value.IsStale.Should().BeFalse();
        result.Value.Error.Should().BeNull();
        result.Value.CommitsPerSecond.Should().Equal(new Dictionary<string, double?> { ["app"] = null, ["analytics"] = null });
        var state = await OverviewSnapshotStore.ReadAsync(_store, Id, default);
        state.LastGood!.Overview.Should().BeEquivalentTo(overview);
        state.LastGood.LoadedAt.Should().Be(OverviewTestData.T0);
    }

    [Fact]
    public async Task Second_call_computes_commits_per_second_from_captured_at()
    {
        _ops.GetOverviewAsync("secret", Arg.Any<CancellationToken>()).Returns(
            OverviewTestData.Overview(OverviewTestData.T0, commits: 100),
            OverviewTestData.Overview(OverviewTestData.T0.AddSeconds(10), commits: 150));
        var sut = Sut();
        await sut.HandleAsync(Id);

        var result = await sut.HandleAsync(Id);

        result.Value!.CommitsPerSecond["app"].Should().Be(5.0);
        result.Value.CommitsPerSecond["analytics"].Should().Be(0.0);
    }

    [Fact]
    public async Task Counter_reset_or_zero_elapsed_gives_null_rate()
    {
        _ops.GetOverviewAsync("secret", Arg.Any<CancellationToken>()).Returns(
            OverviewTestData.Overview(OverviewTestData.T0, commits: 100),
            OverviewTestData.Overview(OverviewTestData.T0.AddSeconds(10), commits: 50),
            OverviewTestData.Overview(OverviewTestData.T0.AddSeconds(10), commits: 60));
        var sut = Sut();
        await sut.HandleAsync(Id);

        (await sut.HandleAsync(Id)).Value!.CommitsPerSecond["app"].Should().BeNull();
        (await sut.HandleAsync(Id)).Value!.CommitsPerSecond["app"].Should().BeNull();
    }

    [Fact]
    public async Task Failure_with_previous_snapshot_returns_stale_view_and_keeps_last_good()
    {
        var good = OverviewTestData.Overview();
        _ops.GetOverviewAsync("secret", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(good), _ => throw new TimeoutException("too slow"));
        var sut = Sut();
        await sut.HandleAsync(Id);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await sut.HandleAsync(Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Overview.Should().BeEquivalentTo(good);
        result.Value.IsStale.Should().BeTrue();
        result.Value.StaleSince.Should().Be(OverviewTestData.T0);
        result.Value.Error.Should().NotBeNullOrEmpty();
        result.Value.ErrorInfo.Should().NotBeNull();
        var state = await OverviewSnapshotStore.ReadAsync(_store, Id, default);
        state.LastGood.Should().NotBeNull();
        state.LastFailureAt.Should().Be(OverviewTestData.T0.AddMinutes(2));
        state.LastError.Should().Be(result.Value.Error);
    }

    [Fact]
    public async Task Failure_without_snapshot_returns_null_overview_and_error()
    {
        _ops.GetOverviewAsync("secret", Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("too slow"));

        var result = await Sut().HandleAsync(Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Overview.Should().BeNull();
        result.Value.IsStale.Should().BeFalse();
        result.Value.StaleSince.Should().BeNull();
        result.Value.Error.Should().NotBeNullOrEmpty();
        result.Value.CommitsPerSecond.Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_and_nothing_is_recorded()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _ops.GetOverviewAsync("secret", Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = () => Sut().HandleAsync(Id, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Store_write_failure_does_not_fail_the_view()
    {
        var overview = OverviewTestData.Overview();
        _ops.GetOverviewAsync("secret", Arg.Any<CancellationToken>()).Returns(overview);
        var store = Substitute.For<IPluginStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        store.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        var sut = new GetOverviewQueryHandler(_ops, _connections, store, _clock, NullLogger<GetOverviewQueryHandler>.Instance);

        var result = await sut.HandleAsync(Id);

        result.Value!.Overview.Should().Be(overview);
    }
}

public class ListDatabasesQueryHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();

    [Fact]
    public async Task Returns_database_names_and_maps_failures()
    {
        var ops = Substitute.For<IPostgresOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");
        ops.ListDatabasesAsync("secret", Arg.Any<CancellationToken>()).Returns(["a", "b"]);
        var sut = new ListDatabasesQueryHandler(ops, connections, NullLogger<ListDatabasesQueryHandler>.Instance);

        (await sut.HandleAsync(Id)).Value.Should().Equal("a", "b");

        ops.ListDatabasesAsync("secret", Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        var failed = await sut.HandleAsync(Id);
        failed.IsSuccess.Should().BeFalse();
        failed.Error.Should().NotBeNullOrEmpty();
    }
}
