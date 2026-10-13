namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>IHostPathPolicy refused the connection's path; the message is the policy's Reason.</summary>
public sealed class SqlitePathRefusedException(string reason) : Exception(reason);

/// <summary>The statement ran past the connection's StatementTimeoutS and was interrupted.</summary>
public sealed class SqliteStatementTimeoutException(int seconds, Exception inner)
    : Exception($"Statement exceeded the {seconds} s statement timeout.", inner);

/// <summary>A table/view name that isn't (or is no longer) in sqlite_schema.</summary>
public sealed class SqliteObjectNotFoundException(string name)
    : Exception($"No table or view named '{name}' — it may have been dropped since the page loaded.");
