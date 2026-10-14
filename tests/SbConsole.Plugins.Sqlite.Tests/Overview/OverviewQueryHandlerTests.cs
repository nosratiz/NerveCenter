using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Overview;

public class OverviewQueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private static readonly SqliteOverview Sample = new(
        "/data/reports.db", "3.53.3", 2_254_857_830, 50_331_648, DateTimeOffset.UnixEpoch,
        4096, 550_502, 46_387, "WAL", "NORMAL", 1000, -2000, 5000);

    [Fact]
    public async Task Overview_returns_the_operations_result_for_a_known_connection()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Sample);

        var result = await new GetOverviewQueryHandler(_operations, _connections, NullLogger<GetOverviewQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Sample);
    }

    [Fact]
    public async Task Overview_fails_without_touching_the_file_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new GetOverviewQueryHandler(_operations, _connections, NullLogger<GetOverviewQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _operations.DidNotReceive().GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Overview_reduces_failures_to_friendly_text()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<SqliteOverview>>(_ => throw new Microsoft.Data.Sqlite.SqliteException("raw", 26));

        var result = await new GetOverviewQueryHandler(_operations, _connections, NullLogger<GetOverviewQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.Error.Should().Be("The file is not a valid or healthy SQLite database.");
    }

    [Fact]
    public async Task Table_sizes_return_the_operations_result()
    {
        var sizes = new TableSizes(false, 1, 0, [new TableSizeEntry("events", SchemaObjectType.Table, null, 41_200_000)]);
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetTableSizesAsync(Secret, Arg.Any<CancellationToken>()).Returns(sizes);

        var result = await new GetTableSizesQueryHandler(_operations, _connections, NullLogger<GetTableSizesQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.Value.Should().Be(sizes);
    }
}
