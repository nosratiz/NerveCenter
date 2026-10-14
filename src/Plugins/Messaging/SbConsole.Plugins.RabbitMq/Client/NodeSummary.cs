namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>GET /api/nodes, one row per cluster node. Byte counts are raw bytes.</summary>
public sealed record NodeSummary(
    string Name,
    bool Running,
    long MemUsed,
    long MemLimit,
    bool MemAlarm,
    long DiskFree,
    long DiskFreeLimit,
    bool DiskAlarm,
    long FdUsed,
    long FdTotal,
    TimeSpan? Uptime);
