using SbConsole.Core.Settings;
using SbConsole.Sdk;

namespace SbConsole.Core.Security;

/// <summary>
/// The host's file allow-list for plugins that open server-side files (today: SQLite). A file is
/// allowed only if, after collapsing ".." and following every symlink on the file and on each
/// parent directory, it sits under one of BootstrapOptions.SqliteRoots (separator-aware, so
/// "/data2/x" is not under "/data"). SbConsole's own database and its -wal/-shm/-journal siblings
/// are always refused, even under a root. No roots configured = everything refused. See
/// docs/design.md §15 and the SQLite plugin design spec §3.
/// </summary>
public sealed class HostPathPolicy(BootstrapOptions options) : IHostPathPolicy, ISingletonService
{
    private const int MaxSymlinkHops = 40;

    public IReadOnlyList<string> AllowedRoots => options.SqliteRoots;

    public PathCheckResult Check(string path)
    {
        if (options.SqliteRoots.Count == 0)
        {
            return PathCheckResult.Refuse("No SQLite roots are configured — set SBC_SQLITE_ROOTS.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return PathCheckResult.Refuse("A file path is required.");
        }

        string resolved;
        try
        {
            resolved = ResolveReal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return PathCheckResult.Refuse("The path could not be resolved.");
        }

        if (IsHostDatabase(resolved))
        {
            return PathCheckResult.Refuse("That file belongs to SbConsole itself and can't be opened as a SQLite connection.");
        }

        foreach (var root in options.SqliteRoots)
        {
            string resolvedRoot;
            try
            {
                resolvedRoot = ResolveReal(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (IsUnder(resolved, resolvedRoot))
            {
                return PathCheckResult.Allow(resolved);
            }
        }

        return PathCheckResult.Refuse($"The path is outside the allowed SQLite roots ({string.Join(", ", options.SqliteRoots)}).");
    }

    /// <summary>
    /// Absolute path with ".." collapsed and every symlink component replaced by its target,
    /// re-resolving the target in turn (it may itself sit under a symlinked directory).
    /// Components that don't exist are kept as-is.
    /// </summary>
    internal static string ResolveReal(string path) => ResolveReal(path, 0);

    private static string ResolveReal(string path, int hops)
    {
        if (hops > MaxSymlinkHops)
        {
            throw new IOException("Too many levels of symbolic links.");
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? Path.DirectorySeparatorChar.ToString();
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = Path.Combine(current, parts[i]);
            var linkTarget = new FileInfo(candidate).LinkTarget;
            if (linkTarget is null)
            {
                current = candidate;
                continue;
            }

            // A relative link target is relative to the directory holding the link.
            var targetFull = Path.GetFullPath(linkTarget, current);
            var rest = parts.Skip(i + 1).Prepend(targetFull).ToArray();
            return ResolveReal(Path.Combine(rest), hops + 1);
        }

        return current;
    }

    private static bool IsUnder(string path, string root)
    {
        var withSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(withSeparator, StringComparison.Ordinal);
    }

    // Case-insensitive on purpose: on a case-insensitive file system (macOS dev boxes) "/Data/x.db"
    // and "/data/x.db" are the same file, and refusing too much here is the safe direction.
    private bool IsHostDatabase(string resolved)
    {
        string db;
        try
        {
            db = ResolveReal(options.DbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            db = Path.GetFullPath(options.DbPath);
        }

        string[] own = [db, db + "-wal", db + "-shm", db + "-journal"];
        return own.Any(o => string.Equals(o, resolved, StringComparison.OrdinalIgnoreCase));
    }
}
