namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>Raw PRAGMA integrity_check rows (capped at MaxMessages) plus the PRAGMA foreign_key_check row count.</summary>
public sealed record IntegrityCheckOutcome(IReadOnlyList<string> Messages, int ForeignKeyViolations)
{
    public const int MaxMessages = 20;

    public bool IntegrityOk => Messages is ["ok"];
}
