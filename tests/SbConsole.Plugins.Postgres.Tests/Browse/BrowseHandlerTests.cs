using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Browse;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Browse;

public class BrowseHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly PgRowsRequest Request = new(42, [new PgFilter("status", PgFilterOp.Eq, "pending")], "id", true, 0, 50);
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public BrowseHandlerTests() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private void NoConnection() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

    [Fact]
    public async Task List_schema_missing_connection_fails_with_not_found()
    {
        NoConnection();
        var result = await new ListSchemaQueryHandler(_ops, _connections, NullLogger<ListSchemaQueryHandler>.Instance).HandleAsync(Id, "app");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
    }

    [Fact]
    public async Task List_schema_returns_the_tree_for_the_database()
    {
        var tree = new PgSchemaTree("app", []);
        _ops.ListSchemaAsync("secret", "app", Arg.Any<CancellationToken>()).Returns(tree);

        var result = await new ListSchemaQueryHandler(_ops, _connections, NullLogger<ListSchemaQueryHandler>.Instance).HandleAsync(Id, "app");

        result.Value.Should().BeSameAs(tree);
    }

    [Fact]
    public async Task Object_detail_returns_null_for_a_dropped_object_and_the_detail_otherwise()
    {
        var detail = new PgObjectDetail(42, "public", "orders", PgObjectKind.Table, 10, 1, 1, "app", [], [], [], null);
        _ops.GetObjectDetailAsync("secret", "app", 42, Arg.Any<CancellationToken>()).Returns(detail);
        _ops.GetObjectDetailAsync("secret", "app", 43, Arg.Any<CancellationToken>()).Returns((PgObjectDetail?)null);
        var sut = new GetObjectDetailQueryHandler(_ops, _connections, NullLogger<GetObjectDetailQueryHandler>.Instance);

        (await sut.HandleAsync(Id, "app", 42)).Value.Should().BeSameAs(detail);
        var gone = await sut.HandleAsync(Id, "app", 43);
        gone.IsSuccess.Should().BeTrue();
        gone.Value.Should().BeNull();
    }

    [Fact]
    public async Task Object_detail_failure_is_friendly()
    {
        _ops.GetObjectDetailAsync("secret", "app", 42, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        var result = await new GetObjectDetailQueryHandler(_ops, _connections, NullLogger<GetObjectDetailQueryHandler>.Instance).HandleAsync(Id, "app", 42);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Can't reach the server");
    }

    [Fact]
    public async Task Ddl_returns_the_text()
    {
        _ops.GetDdlAsync("secret", null, 42, Arg.Any<CancellationToken>()).Returns("CREATE TABLE x ();");
        var result = await new GetDdlQueryHandler(_ops, _connections, NullLogger<GetDdlQueryHandler>.Instance).HandleAsync(Id, null, 42);

        result.Value.Should().Be("CREATE TABLE x ();");
    }

    [Fact]
    public async Task Preview_rows_passes_the_request_through_and_a_refused_filter_fails_with_its_message()
    {
        var page = new PgRowPage([], [], 7);
        _ops.PreviewRowsAsync("secret", "app", Request, Arg.Any<CancellationToken>()).Returns(page);
        var sut = new PreviewRowsQueryHandler(_ops, _connections, NullLogger<PreviewRowsQueryHandler>.Instance);
        (await sut.HandleAsync(Id, "app", Request)).Value.Should().BeSameAs(page);

        _ops.PreviewRowsAsync("secret", "app", Request, Arg.Any<CancellationToken>()).ThrowsAsync(new PgSqlRefusedException("Filter operator not allowed."));
        var refused = await sut.HandleAsync(Id, "app", Request);
        refused.IsSuccess.Should().BeFalse();
        refused.Error.Should().Contain("Filter operator not allowed.");
    }

    private ExportTableCsvCommandHandler Export() => new(_ops, _connections, _audit, NullLogger<ExportTableCsvCommandHandler>.Instance);

    private static ExportTableCsvCommand Command() => new(Id, "pg-prod-eu", "app", "public.orders", Request);

    [Fact]
    public async Task Export_missing_connection_fails_without_auditing()
    {
        NoConnection();
        var result = await Export().HandleAsync(Command());

        result.IsSuccess.Should().BeFalse();
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task Export_success_audits_with_summary_and_filter_count()
    {
        var export = new CsvExport("orders.csv", [1, 2], 2, false);
        _ops.ExportTableCsvAsync("secret", "app", Request, Arg.Any<CancellationToken>()).Returns(export);

        var result = await Export().HandleAsync(Command());

        result.Value.Should().BeSameAs(export);
        await _audit.Received(1).RecordAsync("pg.table.export", "pg-prod-eu/app/public.orders", ActionRisk.Safe, true,
            "2 rows · 1 filters", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Export_success_without_filters_has_no_filter_suffix()
    {
        var request = Request with { Filters = [] };
        _ops.ExportTableCsvAsync("secret", "app", request, Arg.Any<CancellationToken>()).Returns(new CsvExport("o.csv", [], 0, false));

        await Export().HandleAsync(Command() with { Request = request });

        await _audit.Received(1).RecordAsync("pg.table.export", Arg.Any<string>(), ActionRisk.Safe, true, "0 rows", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Export_failure_is_audited_with_friendly_text()
    {
        _ops.ExportTableCsvAsync("secret", "app", Request, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var result = await Export().HandleAsync(Command());

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("pg.table.export", "pg-prod-eu/app/public.orders", ActionRisk.Safe, false,
            Arg.Is<string?>(s => s != null && s.Contains("Can't reach the server")), Arg.Any<CancellationToken>());
    }
}
