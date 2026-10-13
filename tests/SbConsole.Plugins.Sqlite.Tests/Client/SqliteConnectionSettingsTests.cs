using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class SqliteConnectionSettingsTests
{
    [Fact]
    public void Round_trips_through_the_secret_string()
    {
        var settings = new SqliteConnectionSettings("/data/reports.db", SqliteAccessMode.ReadWrite, 2500, 12);

        var secret = settings.ToSecretString();

        secret.Should().Be("Path=/data/reports.db;Mode=ReadWrite;BusyTimeoutMs=2500;StatementTimeoutS=12");
        SqliteConnectionSettings.Parse(secret).Should().Be(settings);
    }

    [Fact]
    public void A_backup_directory_round_trips_and_is_appended_last()
    {
        var settings = new SqliteConnectionSettings("/data/reports.db", BackupDir: "/data/backups");

        var secret = settings.ToSecretString();

        secret.Should().Be("Path=/data/reports.db;Mode=ReadOnly;BusyTimeoutMs=5000;StatementTimeoutS=30;BackupDir=/data/backups");
        SqliteConnectionSettings.Parse(secret).Should().Be(settings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_backup_directory_is_omitted_from_the_secret(string? backupDir)
    {
        new SqliteConnectionSettings("/data/reports.db", BackupDir: backupDir).ToSecretString().Should().NotContain("BackupDir");
    }

    [Fact]
    public void An_empty_backup_directory_parses_as_none()
    {
        SqliteConnectionSettings.Parse("Path=/data/reports.db;BackupDir=").BackupDir.Should().BeNull();
        SqliteConnectionSettings.Parse("Path=/data/reports.db").BackupDir.Should().BeNull();
    }

    [Fact]
    public void Refuses_to_format_a_backup_directory_with_a_semicolon()
    {
        var act = () => new SqliteConnectionSettings("/x.db", BackupDir: "/data/a;b").ToSecretString();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Defaults_mode_and_timeouts_when_only_the_path_is_given()
    {
        var settings = SqliteConnectionSettings.Parse("Path=/data/reports.db");

        settings.Should().Be(new SqliteConnectionSettings("/data/reports.db", SqliteAccessMode.ReadOnly, 5000, 30));
    }

    [Fact]
    public void Keys_are_case_insensitive_and_unknown_keys_are_ignored()
    {
        var settings = SqliteConnectionSettings.Parse("path=/x.db;MODE=readonly;Extra=1");

        settings.Path.Should().Be("/x.db");
        settings.Mode.Should().Be(SqliteAccessMode.ReadOnly);
    }

    [Theory]
    [InlineData("Mode=ReadOnly", "Path is required.")]
    [InlineData("Path=", "Path is required.")]
    [InlineData("Path=/x.db;Mode=Sideways", "Unknown Mode 'Sideways'.")]
    [InlineData("Path=/x.db;Mode=1", "Unknown Mode '1'.")]
    [InlineData("Path=/x.db;Mode=ReadOnly,ReadWrite", "Unknown Mode 'ReadOnly,ReadWrite'.")]
    [InlineData("Path=/x.db;BusyTimeoutMs=soon", "BusyTimeoutMs must be a positive whole number.")]
    [InlineData("Path=/x.db;StatementTimeoutS=0", "StatementTimeoutS must be a positive whole number.")]
    [InlineData("Path=/x.db;garbage", "Expected key=value, got 'garbage'.")]
    public void Rejects_malformed_secrets(string secret, string message)
    {
        var act = () => SqliteConnectionSettings.Parse(secret);

        act.Should().Throw<FormatException>().WithMessage(message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/data/a;b.db")]
    public void Refuses_to_format_an_unrepresentable_path(string path)
    {
        var act = () => new SqliteConnectionSettings(path).ToSecretString();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Refuses_to_format_a_non_positive_timeout()
    {
        var act = () => new SqliteConnectionSettings("/x.db", BusyTimeoutMs: 0).ToSecretString();

        act.Should().Throw<ArgumentException>();
    }
}
