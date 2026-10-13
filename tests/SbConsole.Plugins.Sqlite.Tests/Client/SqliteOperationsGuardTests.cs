using FluentAssertions;
using Microsoft.Data.Sqlite;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Design spec §11.1: the connection guard every user-SQL connection runs behind.</summary>
public sealed class SqliteOperationsGuardTests : IDisposable
{
    private readonly TempDatabase _db = new("CREATE TABLE a(x); INSERT INTO a VALUES (1);");

    [Fact]
    public void Attach_fails_on_an_attach_limited_connection()
    {
        var other = System.IO.Path.Combine(_db.Directory, "other.db");

        var act = () => _db.Operations().Run(_db.Secret(), SqliteOpenOptions.UserQuery, c =>
        {
            SqliteSql.Execute(c, "ATTACH @p AS other", ("@p", other));
            return 0;
        }, CancellationToken.None);

        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(1);
        File.Exists(other).Should().BeFalse();
    }

    [Fact]
    public void Vacuum_into_fails_on_an_attach_limited_connection()
    {
        var copy = System.IO.Path.Combine(_db.Directory, "copy.db");

        var act = () => _db.Operations().Run(_db.Secret(), SqliteOpenOptions.UserQuery, c =>
        {
            SqliteSql.Execute(c, "VACUUM INTO @p", ("@p", copy));
            return 0;
        }, CancellationToken.None);

        act.Should().Throw<SqliteException>();
        File.Exists(copy).Should().BeFalse();
    }

    [Fact]
    public void Vacuum_into_still_works_on_a_plain_read_only_connection()
    {
        // The plugin-owned backup path (§11.7) relies on this; it never runs user SQL.
        var copy = System.IO.Path.Combine(_db.Directory, "copy.db");

        _db.Operations().Run(_db.Secret(), c =>
        {
            SqliteSql.Execute(c, "VACUUM INTO @p", ("@p", copy));
            return 0;
        }, CancellationToken.None);

        File.Exists(copy).Should().BeTrue();
    }

    [Fact]
    public void A_writable_open_on_a_read_only_mode_secret_is_refused_before_opening()
    {
        var act = () => _db.Operations().Run(_db.Secret(mode: SqliteAccessMode.ReadOnly), SqliteOpenOptions.UserWrite, _ => 0, CancellationToken.None);

        act.Should().Throw<SqliteReadOnlyConnectionException>()
            .WithMessage("This connection is read-only — set Default mode to Read-write in Connections.");
    }

    [Fact]
    public void A_writable_open_on_a_read_write_secret_can_write()
    {
        _db.Operations().Run(_db.Secret(mode: SqliteAccessMode.ReadWrite), SqliteOpenOptions.UserWrite, c =>
        {
            SqliteSql.Execute(c, "INSERT INTO a VALUES (2)");
            return 0;
        }, CancellationToken.None);

        using var check = _db.OpenWriter();
        SqliteSql.Long(check, "SELECT COUNT(*) FROM a").Should().Be(2);
    }

    [Fact]
    public void A_writable_open_never_creates_a_missing_file()
    {
        var missing = System.IO.Path.Combine(_db.Directory, "missing.db");
        var secret = new SqliteConnectionSettings(missing, SqliteAccessMode.ReadWrite).ToSecretString();

        var act = () => _db.Operations().Run(secret, SqliteOpenOptions.UserWrite, _ => 0, CancellationToken.None);

        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(14);
        File.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public void The_default_options_stay_read_only_on_a_read_write_secret()
    {
        var act = () => _db.Operations().Run(_db.Secret(mode: SqliteAccessMode.ReadWrite), SqliteOpenOptions.UserQuery, c =>
        {
            SqliteSql.Execute(c, "INSERT INTO a VALUES (2)");
            return 0;
        }, CancellationToken.None);

        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(8);
    }

    [Fact]
    public async Task A_timeout_override_replaces_the_statement_timeout()
    {
        const string forever = "WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x) SELECT count(*) FROM x";
        var options = new SqliteOpenOptions(Timeout: TimeSpan.FromSeconds(1));

        var act = () => _db.Operations().RunAsync(_db.Secret(statementTimeoutS: 600), options, c => SqliteSql.Long(c, forever), CancellationToken.None);

        (await act.Should().ThrowAsync<SqliteStatementTimeoutException>()).Which.Message.Should().Be("Statement exceeded the 1 s statement timeout.");
    }

    public void Dispose() => _db.Dispose();
}
