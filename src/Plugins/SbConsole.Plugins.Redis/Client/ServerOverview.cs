namespace SbConsole.Plugins.Redis.Client;

public sealed record ServerOverview(
    string RedisVersion,
    string Mode,
    int ConnectedReplicas,
    long UsedMemoryBytes,
    long? MaxMemoryBytes,
    string MaxMemoryPolicy,
    double MemoryFragmentationRatio,
    double HitRatioPercent,
    long OpsPerSecond,
    long EvictedKeysTotal,
    long ExpiredKeysTotal,
    long ConnectedClients,
    long BlockedClients,
    IReadOnlyList<KeyspaceDatabaseInfo> Databases);

public sealed record KeyspaceDatabaseInfo(int Index, long KeyCount, long KeysWithExpiry);
