using System.Net.Sockets;
using FluentAssertions;
using Npgsql;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class FriendlyPostgresErrorTests
{
    private static PostgresException Pg(string sqlState, string message, string? hint = null, int position = 0,
        string? table = null, string? column = null, string? constraint = null) =>
        new(messageText: message, severity: "ERROR", invariantSeverity: "ERROR", sqlState: sqlState,
            hint: hint, position: position, tableName: table, columnName: column, constraintName: constraint);

    [Theory]
    [InlineData("42703", "column \"x\" does not exist", "orders", "x", "Column `x` doesn't exist on `orders`.", "undefined_column")]
    [InlineData("42703", "column o.total does not exist", null, null, "Column `total` doesn't exist on `o`.", "undefined_column")]
    [InlineData("42703", "weird", null, null, "That column doesn't exist.", "undefined_column")]
    [InlineData("42P01", "relation \"foo\" does not exist", null, null, "Table `foo` doesn't exist.", "undefined_table")]
    [InlineData("42601", "syntax error at or near \"selec\"", null, null, "Syntax error near `selec`.", "syntax_error")]
    [InlineData("42601", "syntax error at end of input", null, null, "Syntax error.", "syntax_error")]
    [InlineData("25006", "cannot execute INSERT in a read-only transaction", null, null, "This is a read-only transaction — switch to Write to change data.", "read_only_sql_transaction")]
    [InlineData("57014", "canceling statement", null, null, "The statement was cancelled — it hit the timeout or someone cancelled it.", "query_canceled")]
    [InlineData("40P01", "dead", null, null, "Deadlock — the server rolled this statement back.", "deadlock_detected")]
    [InlineData("55P03", "lock", null, null, "The row or table is locked by another session.", "lock_not_available")]
    [InlineData("42501", "denied", null, null, "Permission denied for this role.", "insufficient_privilege")]
    [InlineData("28P01", "pw", null, null, "Password authentication failed.", "invalid_password")]
    [InlineData("3D000", "db", null, null, "Database doesn't exist.", "invalid_catalog_name")]
    [InlineData("53300", "slots", null, null, "The server has no connection slots left.", "too_many_connections")]
    [InlineData("42883", "function foo() does not exist", null, null, "Function foo() does not exist", "undefined_function")]
    public void Maps_postgres_errors(string state, string msg, string? table, string? column, string expected, string condition)
    {
        var info = FriendlyPostgresError.Describe(Pg(state, msg, table: table, column: column));
        info.Message.Should().Be(expected);
        info.SqlState.Should().Be(state);
        info.ConditionName.Should().Be(condition);
        info.IsConnectionFailure.Should().BeFalse();
    }

    [Fact]
    public void Maps_constraint_violations()
    {
        FriendlyPostgresError.From(Pg("23505", "dup", constraint: "pk_a")).Should().Be("That would duplicate a unique key (`pk_a`).");
        FriendlyPostgresError.From(Pg("23503", "fk", constraint: "fk_a")).Should().Be("That would break foreign key `fk_a`.");
        FriendlyPostgresError.From(Pg("23502", "nn", column: "c")).Should().Be("Column `c` can't be null.");
        FriendlyPostgresError.From(Pg("23514", "ck", constraint: "ck_a")).Should().Be("Check constraint `ck_a` failed.");
    }

    [Fact]
    public void Unknown_state_uses_truncated_message_with_capital_letter() =>
        FriendlyPostgresError.From(Pg("XX000", "something broke")).Should().Be("Something broke");

    [Fact]
    public void Describe_carries_the_hint() =>
        FriendlyPostgresError.Describe(Pg("42703", "x", hint: "Perhaps you meant y.")).Hint.Should().Be("Perhaps you meant y.");

    [Fact]
    public void Describe_maps_position_to_line_and_column()
    {
        var info = FriendlyPostgresError.Describe(Pg("42601", "syntax error at or near \"x\"", position: 12), "select 1\nfrom x");
        info.Line.Should().Be(2);
        info.Column.Should().Be(3);
        FriendlyPostgresError.Describe(Pg("42601", "x", position: 12)).Line.Should().BeNull();
    }

    [Fact]
    public void Connection_refused_is_a_connection_failure()
    {
        var ex = new NpgsqlException("Exception while connecting", new SocketException((int)SocketError.ConnectionRefused));
        var info = FriendlyPostgresError.Describe(ex);
        info.IsConnectionFailure.Should().BeTrue();
        info.Message.Should().Be("Can't reach the server — the connection was refused.");
    }

    [Fact]
    public void Timeouts_are_connection_failures()
    {
        FriendlyPostgresError.Describe(new NpgsqlException("x", new TimeoutException()))
            .Message.Should().Be("Can't reach the server — it didn't answer in time.");
        FriendlyPostgresError.Describe(new TimeoutException()).IsConnectionFailure.Should().BeTrue();
        FriendlyPostgresError.Describe(new NpgsqlException("x", new SocketException((int)SocketError.TimedOut)))
            .Message.Should().Be("Can't reach the server — it didn't answer in time.");
    }

    [Fact]
    public void Refusals_pass_their_message_through() =>
        FriendlyPostgresError.From(new PgSqlRefusedException("Nothing to run.")).Should().Be("Nothing to run.");

    [Fact]
    public void Invalid_settings_are_explained() =>
        FriendlyPostgresError.From(new FormatException("bad port")).Should().Be("The saved connection settings are invalid: bad port");

    [Fact]
    public void Other_exceptions_use_FriendlyError() =>
        FriendlyPostgresError.From(new InvalidOperationException("boom")).Should().Be("boom");
}
