using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Query;

/// <summary>Design spec §12: the completion schema is a query -- never audited.</summary>
public class CompletionSchemaQueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    private static readonly CompletionSchema Schema = new(
        [new CompletionObject("events", SchemaObjectType.Table, [new CompletionColumn("id", "INTEGER", true)])], false);

    public CompletionSchemaQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    private GetCompletionSchemaQueryHandler Handler() => new(_operations, _connections, NullLogger<GetCompletionSchemaQueryHandler>.Instance);

    [Fact]
    public async Task Returns_the_live_files_schema()
    {
        _operations.GetCompletionSchemaAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(Schema);

        var result = await Handler().HandleAsync(_connectionId, null);

        result.Value.Should().BeSameAs(Schema);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Passes_the_backup_file()
    {
        _operations.GetCompletionSchemaAsync(Secret, "reports-1.db", Arg.Any<CancellationToken>()).Returns(Schema);

        var result = await Handler().HandleAsync(_connectionId, "reports-1.db");

        result.Value.Should().BeSameAs(Schema);
    }

    [Fact]
    public async Task Failures_are_friendly()
    {
        _operations.GetCompletionSchemaAsync(Secret, "gone.db", Arg.Any<CancellationToken>())
            .Returns<Task<CompletionSchema>>(_ => throw new SqliteBackupException("Backup file not found: gone.db"));

        var result = await Handler().HandleAsync(_connectionId, "gone.db");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("gone.db");
    }

    [Fact]
    public async Task An_unknown_connection_fails_without_touching_the_file()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(unknown, null);

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _operations.DidNotReceiveWithAnyArgs().GetCompletionSchemaAsync(default!, default, default);
    }
}
