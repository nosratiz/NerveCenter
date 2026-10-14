using FluentAssertions;
using Npgsql;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class PostgresConnectionSettingsTests
{
    [Fact]
    public void Parses_a_full_secret()
    {
        var s = PostgresConnectionSettings.Parse(
            "Host=db.local;Port=6543;Database=shop;Username=app;Password=pw;SSL Mode=VerifyFull;Timeout=9;Mode=ReadWrite;StatementTimeoutS=12");

        s.Host.Should().Be("db.local");
        s.Port.Should().Be(6543);
        s.Database.Should().Be("shop");
        s.Username.Should().Be("app");
        s.Password.Should().Be("pw");
        s.SslMode.Should().Be("VerifyFull");
        s.ConnectTimeoutS.Should().Be(9);
        s.StatementTimeoutS.Should().Be(12);
        s.Mode.Should().Be(PgAccessMode.ReadWrite);
    }

    [Fact]
    public void Defaults_apply_when_keys_are_missing()
    {
        var s = PostgresConnectionSettings.Parse("Host=h");

        s.Port.Should().Be(5432);
        s.Database.Should().Be("postgres");
        s.SslMode.Should().Be("Prefer");
        s.ConnectTimeoutS.Should().Be(5);
        s.StatementTimeoutS.Should().Be(30);
        s.Mode.Should().Be(PgAccessMode.ReadOnly);
    }

    [Fact]
    public void Round_trips_through_ToSecretString()
    {
        var original = new PostgresConnectionSettings("h", 5433, "d", "u", "p", "Require", 7, 20, PgAccessMode.ReadWrite);

        PostgresConnectionSettings.Parse(original.ToSecretString()).Should().Be(original);
    }

    [Fact]
    public void Missing_host_is_a_format_error()
    {
        var act = () => PostgresConnectionSettings.Parse("Port=5432");

        act.Should().Throw<FormatException>().WithMessage("Host is required.");
    }

    [Fact]
    public void Unknown_mode_is_a_format_error()
    {
        var act = () => PostgresConnectionSettings.Parse("Host=h;Mode=Admin");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Unknown_ssl_mode_is_a_format_error()
    {
        var act = () => PostgresConnectionSettings.Parse("Host=h;SSL Mode=Allow");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Non_positive_statement_timeout_is_a_format_error()
    {
        var act = () => PostgresConnectionSettings.Parse("Host=h;StatementTimeoutS=0");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ToSecretString_rejects_blank_host_and_bad_port()
    {
        var blank = () => new PostgresConnectionSettings(" ").ToSecretString();
        var port = () => new PostgresConnectionSettings("h", 70000).ToSecretString();

        blank.Should().Throw<ArgumentException>();
        port.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildConnectionString_forces_application_name_and_hides_error_detail()
    {
        var cs = new PostgresConnectionSettings("h", Username: "u", Password: "p", Mode: PgAccessMode.ReadWrite).BuildConnectionString();
        var b = new NpgsqlConnectionStringBuilder(cs);

        b.ApplicationName.Should().Be("sbconsole");
        b.IncludeErrorDetail.Should().BeFalse();
        cs.Should().NotContain(";Mode=").And.NotContain("StatementTimeoutS");
        b.CommandTimeout.Should().Be(35);
    }

    [Fact]
    public void BuildConnectionString_overrides_the_database()
    {
        var s = new PostgresConnectionSettings("h", Database: "a");

        new NpgsqlConnectionStringBuilder(s.BuildConnectionString("b")).Database.Should().Be("b");
        new NpgsqlConnectionStringBuilder(s.BuildConnectionString()).Database.Should().Be("a");
    }

    [Fact]
    public void Password_with_semicolon_round_trips()
    {
        var original = new PostgresConnectionSettings("h", Username: "u", Password: "a;b=c\"d'e");

        PostgresConnectionSettings.Parse(original.ToSecretString()).Password.Should().Be("a;b=c\"d'e");
    }

    [Theory]
    [InlineData("Host=h;Timeout=0")]
    [InlineData("Host=h;Timeout=-3")]
    [InlineData("Host=h;StatementTimeoutS=3601")]
    [InlineData("Host=h;Timeout=1025")]
    public void Present_but_out_of_range_timeouts_are_format_errors(string secret)
    {
        var act = () => PostgresConnectionSettings.Parse(secret);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Absent_timeout_defaults_to_5_and_the_maxima_are_accepted()
    {
        PostgresConnectionSettings.Parse("Host=h").ConnectTimeoutS.Should().Be(5);
        var s = PostgresConnectionSettings.Parse("Host=h;Timeout=1024;StatementTimeoutS=3600");
        s.ConnectTimeoutS.Should().Be(1024);
        s.StatementTimeoutS.Should().Be(3600);
    }

    [Theory]
    [InlineData(1025, 30)]
    [InlineData(5, 3601)]
    public void ToSecretString_rejects_timeouts_above_3600(int connect, int statement)
    {
        var act = () => new PostgresConnectionSettings("h", ConnectTimeoutS: connect, StatementTimeoutS: statement).ToSecretString();

        act.Should().Throw<ArgumentException>();
    }
}
