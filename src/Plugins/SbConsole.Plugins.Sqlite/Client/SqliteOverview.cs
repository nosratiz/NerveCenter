namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// Mockup 1a's tiles. Synchronous, WalAutoCheckpoint, CacheSize and BusyTimeoutMs are
/// per-connection settings -- they describe a fresh connection with this console's settings, not
/// something stored in the file -- and the page labels them that way.
/// </summary>
public sealed record SqliteOverview(
    string Path,
    string SqliteVersion,
    long FileBytes,
    long? WalBytes,
    DateTimeOffset LastModified,
    long PageSize,
    long PageCount,
    long FreelistCount,
    string JournalMode,
    string Synchronous,
    long WalAutoCheckpoint,
    long CacheSize,
    int BusyTimeoutMs)
{
    public long FreeBytes => FreelistCount * PageSize;

    public double FreePercent => PageCount == 0 ? 0 : 100.0 * FreelistCount / PageCount;

    /// <summary>PRAGMA cache_size: positive = pages, negative = KiB.</summary>
    public long CacheBytes => CacheSize >= 0 ? CacheSize * PageSize : -CacheSize * 1024;

    public long CachePages => CacheSize >= 0 ? CacheSize : PageSize == 0 ? 0 : CacheBytes / PageSize;
}
