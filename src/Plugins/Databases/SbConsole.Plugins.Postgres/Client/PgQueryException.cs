using Npgsql;

namespace SbConsole.Plugins.Postgres.Client;

/// <summary>
/// A server error from one statement of a script. <see cref="PostgresException.Position"/> is relative to that
/// statement, so this remembers where the statement starts in the full text (can be negative when the SQL was
/// sent behind a prefix such as EXPLAIN).
/// Display only via FriendlyPostgresError (it unwraps this); never FriendlyError.From directly.
/// </summary>
internal sealed class PgQueryException(PostgresException inner, int statementStart) : Exception(inner.Message, inner)
{
    public PostgresException Inner { get; } = inner;

    public int StatementStart { get; } = statementStart;
}
