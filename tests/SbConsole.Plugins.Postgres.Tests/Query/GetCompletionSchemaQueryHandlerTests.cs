using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Query;

public class GetCompletionSchemaQueryHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    public GetCompletionSchemaQueryHandlerTests() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private GetCompletionSchemaQueryHandler Sut() => new(_ops, _connections, NullLogger<GetCompletionSchemaQueryHandler>.Instance);

    [Fact]
    public async Task Missing_connection_fails_with_not_found()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Sut().HandleAsync(Id, "app");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
    }

    [Fact]
    public async Task Returns_the_schema_for_the_database()
    {
        var schema = new PgCompletionSchema(["public"], ["public"], []);
        _ops.GetCompletionSchemaAsync("secret", "app", Arg.Any<CancellationToken>()).Returns(schema);

        var result = await Sut().HandleAsync(Id, "app");

        result.Value.Should().BeSameAs(schema);
    }

    [Fact]
    public async Task A_failure_becomes_a_friendly_error_and_writes_no_audit_row()
    {
        _ops.GetCompletionSchemaAsync("secret", "app", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Sut().HandleAsync(Id, "app");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }
}
