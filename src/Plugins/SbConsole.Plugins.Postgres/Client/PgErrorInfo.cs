namespace SbConsole.Plugins.Postgres.Client;

public sealed record PgErrorInfo(
    string Message, string? Hint, string? SqlState, string? ConditionName, int? Line, int? Column, bool IsConnectionFailure);
