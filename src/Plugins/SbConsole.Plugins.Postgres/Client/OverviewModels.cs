namespace SbConsole.Plugins.Postgres.Client;

public sealed record PgOverview(
    string ServerVersion, bool IsReplica, DateTimeOffset StartedAt, DateTimeOffset CapturedAt,
    int Connections, int MaxConnections, int Idle, int IdleInTransaction, int Waiting,
    double? CacheHitRatio, string SharedBuffers,
    PgLongestTransaction? LongestTransaction,
    long TotalDatabaseBytes, int DatabaseCount,
    PgReplication Replication,
    long OldestXidAge,
    IReadOnlyList<PgDatabaseStat> Databases,
    PgAttention Attention);
public sealed record PgLongestTransaction(int Pid, string User, string Application, string State, TimeSpan Age);
public sealed record PgReplication(int ReplicaCount, TimeSpan? MaxLag, string? LaggiestName, long? WalRetainedBytes);
public sealed record PgDatabaseStat(string Name, long Bytes, string Owner, int Backends, double? HitRatio, long XactCommit, long Deadlocks);
public sealed record PgAttention(
    int BlockedCount, TimeSpan? LongestBlockedFor, int? TopBlockerPid, string? TopBlockerRelation,
    IReadOnlyList<PgIdleTransaction> IdleInTransaction,          // > 5 min only, longest first
    IReadOnlyList<PgDeadTupleTable> DeadTupleTables,             // > 30 %, worst first, current database
    int UnusedIndexCount, long UnusedIndexBytes, string? LargestUnusedIndex, long? LargestUnusedIndexBytes,
    DateTimeOffset? StatsResetAt);
public sealed record PgIdleTransaction(int Pid, string User, string Application, TimeSpan Age);
public sealed record PgDeadTupleTable(string Name, double DeadPercent);
