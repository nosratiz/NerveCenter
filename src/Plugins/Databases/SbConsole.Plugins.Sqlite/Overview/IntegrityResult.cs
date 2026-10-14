namespace SbConsole.Plugins.Sqlite.Overview;

/// <summary>The last integrity check for one connection, as stored in the plugin store and shown on the Overview strip.</summary>
public sealed record IntegrityResult(
    bool IntegrityOk,
    IReadOnlyList<string> Messages,
    int ForeignKeyViolations,
    DateTimeOffset RanAt,
    TimeSpan Duration);
