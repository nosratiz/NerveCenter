using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public sealed class SqliteOperationsConnectionTests : IDisposable
{
    private const string Forever = "WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x) SELECT count(*) FROM x";
    private readonly TempDatabase _db = new("CREATE TABLE a(x); CREATE TABLE b(y);");

    [Fact]
    public async Task Test_reports_version_size_journal_mode_and_table_count()
    {
        var result = await _db.Operations().TestConnectionAsync(_db.Secret());

        result.Success.Should().BeTrue();
        result.Identity.Should().StartWith("SQLite 3.").And.EndWith("· DELETE");
        result.Checks!.Select(c => (c.Label, c.Status)).Should().Equal(
            ("Path allowed", ConnectionCheckStatus.Passed),
            ("File opened", ConnectionCheckStatus.Passed),
            ("Schema readable", ConnectionCheckStatus.Passed));
        result.Checks![2].Detail.Should().Be("2 tables");
    }

    [Fact]
    public async Task Test_fails_with_the_policy_reason_and_never_opens_a_refused_path()
    {
        var policy = Substitute.For<IHostPathPolicy>();
        policy.Check(Arg.Any<string>()).Returns(PathCheckResult.Refuse("outside the roots"));
        var missing = System.IO.Path.Combine(_db.Directory, "never.db");

        var result = await new SqliteOperations(policy).TestConnectionAsync(new SqliteConnectionSettings(missing).ToSecretString());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("outside the roots");
        result.Checks!.Should().ContainSingle().Which.Status.Should().Be(ConnectionCheckStatus.Failed);
        File.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public async Task Test_fails_friendly_for_a_missing_file_and_does_not_create_it()
    {
        var missing = System.IO.Path.Combine(_db.Directory, "missing.db");

        var result = await _db.Operations().TestConnectionAsync(new SqliteConnectionSettings(missing).ToSecretString());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("File not found, or not readable by the SbConsole container user.");
        File.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public async Task Test_fails_friendly_for_a_file_that_is_not_a_database()
    {
        var text = System.IO.Path.Combine(_db.Directory, "notes.db");
        await File.WriteAllTextAsync(text, new string('x', 4096));

        var result = await _db.Operations().TestConnectionAsync(new SqliteConnectionSettings(text).ToSecretString());

        result.ErrorMessage.Should().Be("The file is not a valid or healthy SQLite database.");
    }

    [Fact]
    public async Task Test_fails_for_unparsable_settings()
    {
        var result = await _db.Operations().TestConnectionAsync("Mode=ReadOnly");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid connection settings: Path is required.");
    }

    [Fact]
    public void Opens_read_only_even_when_the_saved_mode_is_read_write()
    {
        var operations = _db.Operations();

        var act = () => operations.Run(_db.Secret(mode: SqliteAccessMode.ReadWrite), c =>
        {
            SqliteSql.Execute(c, "CREATE TABLE z(x)");
            return 0;
        }, CancellationToken.None);

        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(8);
    }

    [Fact]
    public async Task A_statement_over_the_timeout_is_interrupted_and_reported_as_a_timeout()
    {
        var operations = _db.Operations();
        var stopwatch = Stopwatch.StartNew();

        var act = () => operations.RunAsync(_db.Secret(statementTimeoutS: 1), c => SqliteSql.Long(c, Forever), CancellationToken.None);

        (await act.Should().ThrowAsync<SqliteStatementTimeoutException>())
            .Which.Message.Should().Be("Statement exceeded the 1 s statement timeout.");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_the_statement_and_surfaces_as_cancellation()
    {
        var operations = _db.Operations();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => operations.RunAsync(_db.Secret(), c => SqliteSql.Long(c, Forever), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void A_refused_path_throws_before_anything_is_opened()
    {
        var policy = Substitute.For<IHostPathPolicy>();
        policy.Check(Arg.Any<string>()).Returns(PathCheckResult.Refuse("nope"));

        var act = () => new SqliteOperations(policy).Run(_db.Secret(), _ => 0, CancellationToken.None);

        act.Should().Throw<SqlitePathRefusedException>().WithMessage("nope");
    }

    [Fact]
    public void Quotes_identifiers_by_doubling_embedded_quotes()
    {
        SqliteSql.QuoteIdentifier("we\"ird").Should().Be("\"we\"\"ird\"");
    }

    public void Dispose() => _db.Dispose();
}
