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
