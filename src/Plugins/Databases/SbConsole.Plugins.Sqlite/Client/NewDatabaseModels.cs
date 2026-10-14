namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>The journal mode a new database starts in (design spec §13). WAL is the default.</summary>
public enum NewDatabaseJournal
{
    Wal,
    Delete,
}

/// <summary>What a new database starts with: nothing, the starter schema, or the user's own script.</summary>
public enum NewDatabaseTemplate
{
    Empty,
    Starter,
    Script,
}

/// <summary>
/// "New database…" (spec §13). Folder must be one of IHostPathPolicy.AllowedRoots verbatim;
/// SubFolder is optional, relative, and must already exist under it. FileName gets ".db" appended
/// unless it already ends in .db / .sqlite / .sqlite3. Script is only read for the Script template.
/// </summary>
public sealed record NewDatabaseRequest(
    string Folder,
    string? SubFolder,
    string FileName,
    NewDatabaseJournal Journal = NewDatabaseJournal.Wal,
    NewDatabaseTemplate Template = NewDatabaseTemplate.Empty,
    string? Script = null);

/// <summary>
/// The file that was created. Path is in the configured form (folder / sub-folder / file name) --
/// what the new connection stores, as a person would type it in Connections; ResolvedPath is where
/// the policy resolved it to (what was actually created). Tables = user tables in the new file.
/// </summary>
public sealed record NewDatabaseCreated(string Path, string ResolvedPath, string FileName, int Tables);

/// <summary>The input rules of spec §13, shared by the operation (authoritative) and the dialog (field-level hints).</summary>
public static class NewDatabaseRules
{
    public const int MaxFileNameLength = 200;

    public const string NoRoots = "No SQLite folders are configured — set SBC_SQLITE_ROOTS on the SbConsole host to create databases here.";

    public static readonly IReadOnlyList<string> Extensions = [".db", ".sqlite", ".sqlite3"];

    /// <summary>Null when the (trimmed) name is acceptable; otherwise why not.</summary>
    public static string? FileNameError(string? fileName)
    {
        var name = fileName?.Trim() ?? "";
        if (name.Length == 0)
        {
            return "Type a file name.";
        }

        if (name.IndexOfAny(['/', '\\']) >= 0)
        {
            return "A file name can't contain '/' or '\\' — pick the folder (and sub-folder) instead.";
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return "A file name can't contain '..'.";
        }

        if (name.StartsWith('.'))
        {
            return "A file name can't start with a dot.";
        }

        if (name.Contains(';'))
        {
            return "A file name can't contain ';'.";
        }

        if (name.Any(char.IsControl))
        {
            return "A file name can't contain control characters.";
        }

        return WithExtension(name).Length > MaxFileNameLength ? $"File names are limited to {MaxFileNameLength} characters here." : null;
    }

    /// <summary>The trimmed name, with ".db" appended unless it already has a SQLite extension (any case).</summary>
    public static string WithExtension(string fileName)
    {
        var name = fileName.Trim();
        return Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? name : name + ".db";
    }

    /// <summary>Null when the (trimmed) sub-folder is empty or acceptable: relative, no '.' / '..' parts, no '\', ';' or control characters.</summary>
    public static string? SubFolderError(string? subFolder)
    {
        var sub = subFolder?.Trim() ?? "";
        if (sub.Length == 0)
        {
            return null;
        }

        if (sub.StartsWith('/') || sub.StartsWith('\\') || System.IO.Path.IsPathRooted(sub))
        {
            return "The sub-folder is relative to the folder — drop the leading '/'.";
        }

        if (sub.Contains('\\') || sub.Contains(';') || sub.Any(char.IsControl))
        {
            return "The sub-folder can't contain '\\', ';' or control characters.";
        }

        return sub.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(p => p is "." or "..")
            ? "The sub-folder can't contain '.' or '..' — it must stay inside the folder."
            : null;
    }

    /// <summary>"sqlite-&lt;base&gt;": the file name without its extension (spec §13's default connection name).</summary>
    public static string DefaultConnectionName(string? fileName)
    {
        var name = fileName?.Trim() ?? "";
        if (name.Length == 0)
        {
            return "";
        }

        var withExtension = WithExtension(name);
        return "sqlite-" + withExtension[..withExtension.LastIndexOf('.')];
    }
}
