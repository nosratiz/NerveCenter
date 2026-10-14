using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Query;

public class CheckpointAndExportTests
{
    private const string Secret = "Path=/data/reports.db;Mode=ReadWrite";
    private const string Sql = "SELECT * FROM events";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _unknownId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public CheckpointAndExportTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _connections.GetSecretAsync(_unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);
    }

    private CheckpointWalCommandHandler Checkpoint() => new(_operations, _connections, _audit, NullLogger<CheckpointWalCommandHandler>.Instance);

    private ExportQueryCsvCommandHandler Export() => new(_operations, _connections, _audit, NullLogger<ExportQueryCsvCommandHandler>.Instance);

    private static string Hash(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))[..12];

    [Fact]
    public async Task A_checkpoint_is_audited_as_mutating_with_its_frames()
    {
        _operations.CheckpointWalAsync(Secret, Arg.Any<CancellationToken>()).Returns(new WalCheckpointResult(false, 120, 120));

        var result = await Checkpoint().HandleAsync(new CheckpointWalCommand(_connectionId, "sqlite-reports"));

        result.Value.Should().Be(new WalCheckpointResult(false, 120, 120));
        await _audit.Received(1).RecordAsync("sqlite.wal.checkpoint", "sqlite-reports", ActionRisk.Mutating, true,
            "ok · 120 frames · 120 checkpointed", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_busy_checkpoint_says_so()
    {
        _operations.CheckpointWalAsync(Secret, Arg.Any<CancellationToken>()).Returns(new WalCheckpointResult(true, 120, 80));

        await Checkpoint().HandleAsync(new CheckpointWalCommand(_connectionId, "sqlite-reports"));

        await _audit.Received(1).RecordAsync("sqlite.wal.checkpoint", "sqlite-reports", ActionRisk.Mutating, true,
            "busy · 120 frames · 80 checkpointed", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_checkpoint_is_audited_as_failed()
    {
        _operations.CheckpointWalAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<WalCheckpointResult>>(_ => throw new SqliteReadOnlyConnectionException());

        var result = await Checkpoint().HandleAsync(new CheckpointWalCommand(_connectionId, "sqlite-reports"));

        result.Error.Should().Be("This connection is read-only — set Default mode to Read-write in Connections.");
        await _audit.Received(1).RecordAsync("sqlite.wal.checkpoint", "sqlite-reports", ActionRisk.Mutating, false,
            "This connection is read-only — set Default mode to Read-write in Connections.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_checkpoint_on_an_unknown_connection_is_not_audited()
    {
        var result = await Checkpoint().HandleAsync(new CheckpointWalCommand(_unknownId, "sqlite-reports"));

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_cancelled_checkpoint_rethrows()
    {
        using var cts = new CancellationTokenSource();
        _operations.CheckpointWalAsync(Secret, Arg.Any<CancellationToken>()).Returns<Task<WalCheckpointResult>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Checkpoint().HandleAsync(new CheckpointWalCommand(_connectionId, "sqlite-reports"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_query_export_is_capped_and_audited_as_safe_with_rows_and_hash()
    {
        var export = new CsvExport("query.csv", [1], 1_234, Truncated: true);
        _operations.ExportQueryCsvAsync(Secret, Sql, CsvExport.MaxRows, CsvExport.MaxBytes, null, Arg.Any<CancellationToken>()).Returns(export);

        var result = await Export().HandleAsync(new ExportQueryCsvCommand(_connectionId, "sqlite-reports", Sql));

        result.Value.Should().BeSameAs(export);
        await _audit.Received(1).RecordAsync("sqlite.query.export", "sqlite-reports", ActionRisk.Safe, true,
            $"1,234 rows (truncated at 50 MB) · sha256:{Hash(Sql)} · {Sql}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_query_export_from_a_backup_names_the_backup_in_the_audit_row()
    {
        var export = new CsvExport("query.csv", [1], 3, Truncated: false);
        _operations.ExportQueryCsvAsync(Secret, Sql, CsvExport.MaxRows, CsvExport.MaxBytes, "reports-20261013-020000.db", Arg.Any<CancellationToken>()).Returns(export);

        var result = await Export().HandleAsync(new ExportQueryCsvCommand(_connectionId, "sqlite-reports", Sql, "reports-20261013-020000.db"));

        result.Value.Should().BeSameAs(export);
        await _audit.Received(1).RecordAsync("sqlite.query.export", "sqlite-reports", ActionRisk.Safe, true,
            Arg.Is<string>(d => d.StartsWith("backup reports-20261013-020000.db · 3 rows", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_query_export_is_audited_as_failed()
    {
        _operations.ExportQueryCsvAsync(Secret, Sql, CsvExport.MaxRows, CsvExport.MaxBytes, null, Arg.Any<CancellationToken>())
            .Returns<Task<CsvExport>>(_ => throw new SqliteStatementTimeoutException(30));

        var result = await Export().HandleAsync(new ExportQueryCsvCommand(_connectionId, "sqlite-reports", Sql));

        result.Error.Should().Be("Statement exceeded the 30 s statement timeout.");
        await _audit.Received(1).RecordAsync("sqlite.query.export", "sqlite-reports", ActionRisk.Safe, false,
            $"sha256:{Hash(Sql)} · Statement exceeded the 30 s statement timeout.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_query_export_on_an_unknown_connection_is_not_audited()
    {
        var result = await Export().HandleAsync(new ExportQueryCsvCommand(_unknownId, "sqlite-reports", Sql));

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_cancelled_query_export_rethrows()
    {
        using var cts = new CancellationTokenSource();
        _operations.ExportQueryCsvAsync(Secret, Sql, Arg.Any<int>(), Arg.Any<long>(), null, Arg.Any<CancellationToken>()).Returns<Task<CsvExport>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Export().HandleAsync(new ExportQueryCsvCommand(_connectionId, "sqlite-reports", Sql), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
