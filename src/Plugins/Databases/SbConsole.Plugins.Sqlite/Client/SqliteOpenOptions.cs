namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// How SqliteOperations.Run opens its connection (design spec §11.1). Plugin = phase 1's
/// read-only connection for plugin-owned SQL. Anything that runs user-supplied SQL must use
/// UserQuery or UserWrite, which take away the attach slots. Writable is refused unless the saved
/// secret's Mode is ReadWrite. Timeout replaces the connection's statement timeout (backups).
/// BackupFile opens that file inside the connection's BackupDir (policy-checked) instead of the
/// live file, always read-only (§11.7 "Open read-only").
/// </summary>
internal readonly record struct SqliteOpenOptions(bool Writable = false, bool LimitAttach = false, TimeSpan? Timeout = null, string? BackupFile = null)
{
    /// <summary>VACUUM INTO, verify and restore run under this instead of the statement timeout (§11.7).</summary>
    public static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(30);

    public static SqliteOpenOptions Plugin => default;

    public static SqliteOpenOptions PluginWrite => new(Writable: true);

    public static SqliteOpenOptions UserQuery => new(LimitAttach: true);

    public static SqliteOpenOptions UserWrite => new(Writable: true, LimitAttach: true);

    /// <summary>Plugin-owned and read-only, NOT attach-limited (VACUUM INTO needs an attach slot); never runs user SQL.</summary>
    public static SqliteOpenOptions Backup => new(Timeout: BackupTimeout);

    /// <summary>A backup file, read-only, plugin-owned SQL only.</summary>
    public static SqliteOpenOptions BackupRead(string fileName) => new(Timeout: BackupTimeout, BackupFile: fileName);
}
