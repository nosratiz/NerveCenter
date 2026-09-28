using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Overview;

public class GetMemoryByPrefixQueryHandlerTests
{
    private const string Secret = "standalone|0|localhost:6379";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IRedisOperations _operations = Substitute.For<IRedisOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private GetMemoryByPrefixQueryHandler CreateHandler() =>
        new(_operations, _connections, NullLogger<GetMemoryByPrefixQueryHandler>.Instance);

    [Fact]
    public async Task Returns_entries_for_a_known_connection()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        IReadOnlyList<MemoryByPrefixEntry> entries = [new("session:", 4000, 2, 1)];
        _operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>()).Returns(entries);

        var result = await CreateHandler().HandleAsync(_connectionId, database: 0, ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task Fails_without_calling_the_broker_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateHandler().HandleAsync(_connectionId, database: 0, ct: CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await _operations.DidNotReceive().GetMemoryByPrefixAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<char>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
