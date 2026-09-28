using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Overview;

public class GetSlowLogQueryHandlerTests
{
    private const string Secret = "standalone|0|localhost:6379";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IRedisOperations _operations = Substitute.For<IRedisOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private GetSlowLogQueryHandler CreateHandler() =>
        new(_operations, _connections, NullLogger<GetSlowLogQueryHandler>.Instance);

    [Fact]
    public async Task Returns_entries_for_a_known_connection()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        IReadOnlyList<SlowLogEntry> entries = [new(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(412), "KEYS session:*", "10.4.8.31")];
        _operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>()).Returns(entries);

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task Fails_without_calling_the_broker_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await _operations.DidNotReceive().GetSlowLogAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
