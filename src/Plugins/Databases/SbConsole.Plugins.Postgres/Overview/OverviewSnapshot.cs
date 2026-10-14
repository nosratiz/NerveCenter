using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Overview;

public sealed record OverviewSnapshot(PgOverview Overview, DateTimeOffset LoadedAt);

public sealed record OverviewState(OverviewSnapshot? LastGood, DateTimeOffset? LastFailureAt, string? LastError);

/// <summary>
/// What the Overview page renders. A failed refresh is data, not a failed result: the page still wants
/// the last good snapshot (flagged stale) alongside the error. <c>CommitsPerSecond</c> is keyed by
/// database name; null means there is no previous sample to diff against.
/// </summary>
public sealed record OverviewView(
    PgOverview? Overview, bool IsStale, DateTimeOffset? StaleSince, string? Error, PgErrorInfo? ErrorInfo,
    IReadOnlyDictionary<string, double?> CommitsPerSecond);
