using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Query;

public class RunWriteCommandHandlerTests
{
    private const string Secret = "Path=/data/reports.db;Mode=ReadWrite";
    private const string Sql = "DELETE FROM events WHERE tenant_id = 7";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    private RunWriteCommandHandler Handler() => new(_operations, _connections, _audit, NullLogger<RunWriteCommandHandler>.Instance);

    private RunWriteCommand Command(string sql = Sql) => new(_connectionId, "sqlite-reports", sql);

    private static string Hash(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))[..12];

    [Fact]
    public async Task A_committed_write_is_audited_as_destructive_with_hash_rows_duration_and_sql()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        var written = new WriteScriptResult(true,
            [new WriteStatementResult("DELETE", Sql, 1_204, 3_000), new WriteStatementResult("UPDATE", "UPDATE x SET y = 1", 2, 0)],
            TimeSpan.FromMilliseconds(412), new WriteFileState(4096, 12.5));
        _operations.RunWriteAsync(Secret, Sql, Arg.Any<CancellationToken>()).Returns(written);

        var result = await Handler().HandleAsync(Command());

        result.Value.Should().BeSameAs(written);
        await _audit.Received(1).RecordAsync("sqlite.query.write", "sqlite-reports", ActionRisk.Destructive, true,
            $"sha256:{Hash(Sql)} · 1,206 rows + 3,000 cascaded · 412 ms · {Sql}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_audit_detail_keeps_the_first_200_characters_of_the_sql_on_one_line()
    {
        var sql = "UPDATE events\n   SET name = 'x'\n WHERE " + string.Join(" OR ", Enumerable.Range(0, 60).Select(i => $"id = {i}"));
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunWriteAsync(Secret, sql, Arg.Any<CancellationToken>())
            .Returns(new WriteScriptResult(true, [new WriteStatementResult("UPDATE", sql, 60, 0)], TimeSpan.FromSeconds(2), null));

        await Handler().HandleAsync(Command(sql));

        var detail = (string)_audit.ReceivedCalls().Single().GetArguments()[4]!;
        var preview = detail.Split(" · ", 4)[3];
        preview.Should().StartWith("UPDATE events SET name = 'x' WHERE id = 0 OR").And.EndWith("…").And.NotContain("\n");
        preview.Length.Should().Be(201);
    }

    [Fact]
    public async Task A_failed_write_is_audited_as_failed_with_the_hash_and_friendly_error()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunWriteAsync(Secret, Sql, Arg.Any<CancellationToken>())
            .Returns<Task<WriteScriptResult>>(_ => throw new SqliteException("raw engine noise", 5));

        var result = await Handler().HandleAsync(Command());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Database is locked — still busy after the busy timeout. Another writer holds the lock.");
        await _audit.Received(1).RecordAsync("sqlite.query.write", "sqlite-reports", ActionRisk.Destructive, false,
            $"sha256:{Hash(Sql)} · Database is locked — still busy after the busy timeout. Another writer holds the lock.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_timed_out_write_is_audited_as_failed()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunWriteAsync(Secret, Sql, Arg.Any<CancellationToken>())
            .Returns<Task<WriteScriptResult>>(_ => throw new SqliteStatementTimeoutException(30));

        var result = await Handler().HandleAsync(Command());

        result.Error.Should().Be("Statement exceeded the 30 s statement timeout.");
        await _audit.Received(1).RecordAsync("sqlite.query.write", "sqlite-reports", ActionRisk.Destructive, false,
            Arg.Is<string>(d => d.EndsWith("Statement exceeded the 30 s statement timeout.", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_connection_fails_without_auditing()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(Command());

        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default);
        await _operations.DidNotReceiveWithAnyArgs().RunWriteAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_secret_lookup_failure_is_a_friendly_failed_result_without_an_audit_row()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new CryptographicException("The payload was invalid."));

        var result = await Handler().HandleAsync(Command());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_cancelled_write_rethrows_without_auditing()
    {
        using var cts = new CancellationTokenSource();
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.RunWriteAsync(Secret, Sql, Arg.Any<CancellationToken>()).Returns<Task<WriteScriptResult>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Handler().HandleAsync(Command(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task An_audit_write_failure_does_not_hide_the_committed_result()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        var written = new WriteScriptResult(true, [], TimeSpan.Zero, null);
        _operations.RunWriteAsync(Secret, Sql, Arg.Any<CancellationToken>()).Returns(written);
        _audit.RecordAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs<Task>(_ => throw new InvalidOperationException("db down"));

        var result = await Handler().HandleAsync(Command());

        result.Value.Should().BeSameAs(written);
    }
}
