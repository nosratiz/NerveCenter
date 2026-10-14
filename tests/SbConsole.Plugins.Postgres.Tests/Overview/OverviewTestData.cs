using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Overview;

internal static class OverviewTestData
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public static PgAttention NoAttention() => new(0, null, null, null, [], [], 0, 0, null, null, null);

    public static PgOverview Overview(
        DateTimeOffset? captured = null, long commits = 100, int connections = 10, int maxConnections = 100,
        PgAttention? attention = null, PgReplication? replication = null, long xidAge = 1000,
        bool limitedVisibility = false, IReadOnlyList<PgDatabaseStat>? databases = null) =>
        new("16.4", false, T0.AddDays(-3), captured ?? T0, connections, maxConnections, 2, 1, 0,
            0.99, "128MB", new PgLongestTransaction(42, "app", "worker", "active", TimeSpan.FromMinutes(7)),
            123_456_789, 2, replication ?? new PgReplication(0, null, null, null), xidAge,
            databases ?? [new PgDatabaseStat("app", 1000, "owner", 3, 0.97, commits, 0), new PgDatabaseStat("analytics", 2000, "owner", 1, null, 5, 1)],
            attention ?? NoAttention(), limitedVisibility);
}
