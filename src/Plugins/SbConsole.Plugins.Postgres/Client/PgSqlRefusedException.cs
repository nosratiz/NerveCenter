namespace SbConsole.Plugins.Postgres.Client;

/// <summary>User SQL the console refuses to run; the message is already friendly.</summary>
public sealed class PgSqlRefusedException(string message) : Exception(message);
