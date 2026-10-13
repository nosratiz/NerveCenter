using Microsoft.Data.Sqlite;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Maps SQLite failures to the mockup's 1g wording (design spec §7, §11). Raw engine text never reaches the UI.</summary>
public static class FriendlySqliteError
{
    private const string Locked = "Database is locked — still busy after the busy timeout. Another writer holds the lock.";

    public static string From(Exception ex) => ex switch
    {
        SqlitePathRefusedException or SqliteStatementTimeoutException or SqliteObjectNotFoundException
            or SqliteReadOnlyConnectionException or SqliteScriptRefusedException or SqliteBackupException or SqliteDdlRefusedException => ex.Message,
        SqliteException { SqliteErrorCode: 5 or 6 } => Locked,                          // SQLITE_BUSY / SQLITE_LOCKED
        SqliteException { SqliteErrorCode: 8 } => "This connection is read-only.",      // SQLITE_READONLY
        SqliteException { SqliteErrorCode: 11 or 26 } =>                               // SQLITE_CORRUPT / SQLITE_NOTADB
            "The file is not a valid or healthy SQLite database.",
        SqliteException { SqliteErrorCode: 13 } => "The disk or volume is full.",     // SQLITE_FULL
        SqliteException { SqliteErrorCode: 14 } =>                                     // SQLITE_CANTOPEN
            "File not found, or not readable by the SbConsole container user.",
        FormatException format => $"Invalid connection settings: {FriendlyError.Truncate(format.Message)}",
        _ => FriendlyError.From(ex),
    };
}
