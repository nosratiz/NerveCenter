using FluentAssertions;
using Microsoft.Data.Sqlite;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class FriendlySqliteErrorTests
{
    [Theory]
    [InlineData(5, "Database is locked — still busy after the busy timeout. Another writer holds the lock.")]
    [InlineData(6, "Database is locked — still busy after the busy timeout. Another writer holds the lock.")]
    [InlineData(8, "This connection is read-only.")]
    [InlineData(11, "The file is not a valid or healthy SQLite database.")]
    [InlineData(26, "The file is not a valid or healthy SQLite database.")]
    [InlineData(14, "File not found, or not readable by the SbConsole container user.")]
    [InlineData(13, "The disk or volume is full.")]
    public void Maps_sqlite_error_codes(int code, string expected)
    {
        FriendlySqliteError.From(new SqliteException("raw engine noise", code)).Should().Be(expected);
    }

    [Fact]
    public void Passes_through_the_plugins_own_exceptions()
    {
        FriendlySqliteError.From(new SqlitePathRefusedException("outside the roots")).Should().Be("outside the roots");
        FriendlySqliteError.From(new SqliteObjectNotFoundException("events"))
            .Should().Be("No table or view named 'events' — it may have been dropped since the page loaded.");
        FriendlySqliteError.From(new SqliteReadOnlyConnectionException())
            .Should().Be("This connection is read-only — set Default mode to Read-write in Connections.");
        FriendlySqliteError.From(new SqliteScriptRefusedException("The script is empty.")).Should().Be("The script is empty.");
        FriendlySqliteError.From(new SqliteDdlRefusedException("Type a new name.")).Should().Be("Type a new name.");
        FriendlySqliteError.From(new SqliteNewDatabaseRefusedException("Type a file name.")).Should().Be("Type a file name.");
    }

    [Fact]
    public void Prefixes_settings_errors()
    {
        FriendlySqliteError.From(new FormatException("Path is required.")).Should().Be("Invalid connection settings: Path is required.");
    }

    [Fact]
    public void Falls_back_to_the_sdk_helper_for_anything_else()
    {
        FriendlySqliteError.From(new InvalidOperationException("boom")).Should().Be("boom");
    }
}
