using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

public class SchemaQueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    public SchemaQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    [Fact]
    public async Task Listing_returns_the_operations_result()
    {
        var listing = new SchemaListing([new SchemaObject("events", SchemaObjectType.Table, "events", 41_200_000, null)], false);
        _operations.ListSchemaAsync(Secret, Arg.Any<CancellationToken>()).Returns(listing);

        var result = await new ListSchemaQueryHandler(_operations, _connections, NullLogger<ListSchemaQueryHandler>.Instance)
            .HandleAsync(_connectionId);

        result.Value.Should().Be(listing);
    }

    [Fact]
    public async Task Detail_passes_the_table_name_and_maps_not_found()
    {
        _operations.GetTableDetailAsync(Secret, "gone", Arg.Any<CancellationToken>())
            .Returns<Task<TableDetail>>(_ => throw new SqliteObjectNotFoundException("gone"));

        var result = await new GetTableDetailQueryHandler(_operations, _connections, NullLogger<GetTableDetailQueryHandler>.Instance)
            .HandleAsync(_connectionId, "gone");

        result.Error.Should().Be("No table or view named 'gone' — it may have been dropped since the page loaded.");
    }
}
