using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

public class ExportTableCsvCommandHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    private ExportTableCsvCommandHandler Handler() =>
        new(_operations, _connections, _audit, NullLogger<ExportTableCsvCommandHandler>.Instance);

    [Fact]
    public async Task Export_is_capped_and_audited_with_the_row_count()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        var export = new CsvExport("events.csv", [1, 2], 100_000, Truncated: true);
        _operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>()).Returns(export);

        var result = await Handler().HandleAsync(new ExportTableCsvCommand(_connectionId, "sqlite-reports", "events"));

        result.Value.Should().BeSameAs(export);
        await _audit.Received(1).RecordAsync("sqlite.table.export", "sqlite-reports/events", ActionRisk.Safe, true,
            "100,000 rows (truncated at 100,000 rows)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_export_is_audited_as_failed()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>())
            .Returns<Task<CsvExport>>(_ => throw new SqliteObjectNotFoundException("events"));

        var result = await Handler().HandleAsync(new ExportTableCsvCommand(_connectionId, "sqlite-reports", "events"));

        result.IsSuccess.Should().BeFalse();
        await _audit.Received(1).RecordAsync("sqlite.table.export", "sqlite-reports/events", ActionRisk.Safe, false,
            "No table or view named 'events' — it may have been dropped since the page loaded.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_secret_lookup_failure_is_a_friendly_failed_result_without_an_audit_row()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new System.Security.Cryptography.CryptographicException("The payload was invalid."));

        var result = await Handler().HandleAsync(new ExportTableCsvCommand(_connectionId, "sqlite-reports", "events"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
        await _operations.DidNotReceiveWithAnyArgs().ExportCsvAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task A_byte_capped_export_says_so_in_the_audit_detail()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        var export = new CsvExport("events.csv", [1, 2], 1_234, Truncated: true);
        _operations.ExportCsvAsync(Secret, "events", CsvExport.MaxRows, CsvExport.MaxBytes, Arg.Any<CancellationToken>()).Returns(export);

        await Handler().HandleAsync(new ExportTableCsvCommand(_connectionId, "sqlite-reports", "events"));

        await _audit.Received(1).RecordAsync("sqlite.table.export", "sqlite-reports/events", ActionRisk.Safe, true,
            "1,234 rows (truncated at 50 MB)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_fails_without_auditing()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(new ExportTableCsvCommand(_connectionId, "sqlite-reports", "events"));

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default);
    }
}
