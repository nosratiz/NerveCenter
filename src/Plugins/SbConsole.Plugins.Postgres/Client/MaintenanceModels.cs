namespace SbConsole.Plugins.Postgres.Client;

public enum PgMaintenanceKind { Vacuum, Analyze, VacuumFull }
public sealed record PgVacuumProgress(int Pid, string Table, DateTimeOffset? StartedAt, double? HeapScannedPercent);
public sealed record PgTableHealth(uint Oid, string Schema, string Name, long TotalBytes, long Live, long Dead, double DeadPercent,
    long? BloatBytes, DateTimeOffset? LastVacuum, DateTimeOffset? LastAutovacuum, DateTimeOffset? LastAnalyze,
    DateTimeOffset? LastAutoanalyze, bool AutovacuumRunning);
public sealed record PgMaintenance(bool AutovacuumOn, int MaxWorkers, IReadOnlyList<PgVacuumProgress> Running,
    long OldestXidAge, long FreezeMaxAge, bool PgstattupleAvailable, IReadOnlyList<PgTableHealth> Tables,
    DateTimeOffset CapturedAt = default);
