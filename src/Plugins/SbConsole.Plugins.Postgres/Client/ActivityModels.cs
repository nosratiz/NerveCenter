namespace SbConsole.Plugins.Postgres.Client;

public sealed record PgBackend(int Pid, string User, string Application, string? Client, string State, string? WaitEvent,
    string Query, DateTimeOffset? XactStart, DateTimeOffset? QueryStart, TimeSpan? Duration,
    IReadOnlyList<int> BlockedBy, string? WaitingOnRelation, string? Database);
public sealed record PgActivity(int OwnPid, DateTimeOffset CapturedAt, IReadOnlyList<PgBackend> Backends);
