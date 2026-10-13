namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// How SqliteOperations.Run opens its connection (design spec §11.1). Plugin = phase 1's
/// read-only connection for plugin-owned SQL. Anything that runs user-supplied SQL must use
/// UserQuery or UserWrite, which take away the attach slots. Writable is refused unless the saved
/// secret's Mode is ReadWrite. Timeout replaces the connection's statement timeout (backups).
/// </summary>
internal readonly record struct SqliteOpenOptions(bool Writable = false, bool LimitAttach = false, TimeSpan? Timeout = null)
{
    public static SqliteOpenOptions Plugin => default;

    public static SqliteOpenOptions PluginWrite => new(Writable: true);

    public static SqliteOpenOptions UserQuery => new(LimitAttach: true);

    public static SqliteOpenOptions UserWrite => new(Writable: true, LimitAttach: true);
}
