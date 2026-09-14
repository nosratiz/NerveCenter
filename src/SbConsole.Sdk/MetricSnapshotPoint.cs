namespace SbConsole.Sdk;

/// <summary>
/// One historical reading of a resource's Active/DeadLetter counts, as persisted (a JSON array of
/// these, oldest first) under a plugin's IPluginStore key. Shared shape between the host's
/// background collector (which appends points) and a plugin's own UI (which reads them back to
/// draw trend sparklines) so both sides serialize identically.
/// </summary>
public sealed record MetricSnapshotPoint(DateTimeOffset At, long ActiveCount, long DeadLetterCount);
