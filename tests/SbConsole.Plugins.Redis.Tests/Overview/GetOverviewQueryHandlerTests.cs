using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Overview;

public class GetOverviewQueryHandlerTests
{
    private const string Secret = "standalone|0|localhost:6379";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IRedisOperations _operations = Substitute.For<IRedisOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private GetOverviewQueryHandler CreateHandler() =>
        new(_operations, _connections, NullLogger<GetOverviewQueryHandler>.Instance);

    private static ServerOverview SampleOverview() => new(
        "7.2.4", "standalone", 2, 100, 200, "allkeys-lru", 1.1, 94.2, 41800, 1240000, 98213, 412, 3, []);

    [Fact]
    public async Task Returns_the_overview_for_a_known_connection()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(SampleOverview());

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(SampleOverview());
    }

    [Fact]
    public async Task Fails_without_calling_the_broker_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _operations.DidNotReceive().GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_a_friendly_error_when_the_broker_call_throws()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<ServerOverview>>(_ => throw new InvalidOperationException("raw broker noise"));

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
