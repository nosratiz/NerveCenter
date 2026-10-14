namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>IHostPathPolicy refused the connection's path; the message is the policy's Reason.</summary>
public sealed class SqlitePathRefusedException(string reason) : Exception(reason);

/// <summary>The statement ran past the connection's StatementTimeoutS and was interrupted.</summary>
public sealed class SqliteStatementTimeoutException(int seconds, Exception? inner = null)
    : Exception($"Statement exceeded the {seconds} s statement timeout.", inner);

/// <summary>A table/view name that isn't (or is no longer) in sqlite_schema.</summary>
public sealed class SqliteObjectNotFoundException(string name)
    : Exception($"No table or view named '{name}' — it may have been dropped since the page loaded.");

/// <summary>A writable open was asked for on a connection whose saved Mode isn't ReadWrite (design spec §11.1).</summary>
public sealed class SqliteReadOnlyConnectionException()
    : Exception("This connection is read-only — set Default mode to Read-write in Connections.");

/// <summary>A user script SqliteScript refuses before anything runs (empty, ATTACH, VACUUM, transaction control -- spec §11.2).</summary>
public sealed class SqliteScriptRefusedException(string reason) : Exception(reason);

/// <summary>A backup the plugin refuses or can't find (backups disabled, missing directory, bad file name, failed verify -- spec §11.7).</summary>
public sealed class SqliteBackupException(string reason) : Exception(reason);

/// <summary>A Create / Rename / Drop table request the plugin refuses before (or instead of) changing anything (spec §11.5).</summary>
public sealed class SqliteDdlRefusedException(string reason) : Exception(reason);

/// <summary>A New database request refused before anything touched the disk: folder, sub-folder, file name or an existing file (spec §13).</summary>
public sealed class SqliteNewDatabaseRefusedException(string reason) : Exception(reason);
