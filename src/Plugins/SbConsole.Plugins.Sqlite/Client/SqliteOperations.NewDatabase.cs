using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// New database (design spec §13) -- the only user-initiated file creation besides backups and
/// recovered copies. Everything is validated before the disk is touched: the folder is one of
/// IHostPathPolicy.AllowedRoots verbatim, an optional sub-folder must already exist and stay inside
/// it, the file name follows NewDatabaseRules, the full path passes the policy, and neither the
/// file nor a -wal/-shm/-journal sibling exists. The file is then created exclusively
/// (FileMode.CreateNew, i.e. O_CREAT|O_EXCL) on the resolved path, so a file that appears after the
/// check is never adopted or overwritten, and only then opened writable and attach-limited for the
/// journal mode and the template. No directory is ever created.
/// </summary>
public sealed partial class SqliteOperations
{
    private static readonly string[] SiblingSuffixes = ["-wal", "-shm", "-journal"];

    public Task<NewDatabaseCreated> CreateDatabaseAsync(NewDatabaseRequest request, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(request);
            // A refused script never gets as far as a file (same refusals as the Query page, §11.2).
            var script = request.Template == NewDatabaseTemplate.Script ? SqliteScript.Parse(request.Script ?? "") : null;
            var directory = ResolveNewDatabaseDirectory(request.Folder, request.SubFolder);
            if (NewDatabaseRules.FileNameError(request.FileName) is { } nameError)
            {
                throw new SqliteNewDatabaseRefusedException(nameError);
            }

            var fileName = NewDatabaseRules.WithExtension(request.FileName);
            var path = Path.Combine(directory, fileName);
            var check = pathPolicy.Check(path);
            if (!check.Allowed)
            {
                throw new SqliteNewDatabaseRefusedException(check.Reason ?? "Path refused.");
            }

            var resolved = check.ResolvedPath!;
            if (resolved.Contains(';'))
            {
                throw new SqliteNewDatabaseRefusedException("Paths containing ';' can't be saved as a connection.");
            }

            RequireNoExistingFile(resolved, fileName);
            CreateExclusively(resolved, fileName);
            try
            {
                var secret = new SqliteConnectionSettings(resolved, SqliteAccessMode.ReadWrite).ToSecretString();
                var tables = Run(secret, SqliteOpenOptions.UserWrite, c =>
                {
                    SetJournalMode(c, request.Journal);
                    InTransaction(c, () =>
                    {
                        foreach (var statement in TemplateStatements(request.Template, script))
                        {
                            SqliteSql.Execute(c, statement.Text);
                        }

                        return true;
                    });
                    return UserTables(c).Count;
                }, ct);

                _logger.LogInformation("Created SQLite database {Path} ({Template}, {Journal}).", resolved, request.Template, request.Journal);
                return new NewDatabaseCreated(path, resolved, fileName, tables);
            }
            catch
            {
                DeleteDatabaseFiles(resolved);
                throw;
            }
        }, ct);

    public Task DeleteCreatedDatabaseAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var check = pathPolicy.Check(path);
            if (!check.Allowed)
            {
                throw new SqliteNewDatabaseRefusedException(check.Reason ?? "Path refused.");
            }

            var resolved = check.ResolvedPath!;
            if (!NewDatabaseRules.Extensions.Any(e => resolved.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqliteNewDatabaseRefusedException("Only a database file this console created can be removed here.");
            }

            DeleteDatabaseFiles(resolved);
        }, ct);

    public Task<string?> FindBackupsDirAsync(string folder, string? subFolder, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string directory;
            try
            {
                directory = ResolveNewDatabaseDirectory(folder, subFolder);
            }
            catch (SqliteNewDatabaseRefusedException)
            {
                return null;
            }

            var backups = Path.Combine(directory, "backups");
            var check = pathPolicy.Check(backups);
            return check.Allowed && Directory.Exists(check.ResolvedPath) ? backups : null;
        }, ct);

    /// <summary>
    /// The target directory in its configured form: the root exactly as configured (string match),
    /// plus the optional sub-folder, which must be relative, free of '.'/'..' parts, lexically inside
    /// the root, and already exist. The full file path is policy-checked afterwards, which also
    /// follows symlinks; a symlinked sub-folder therefore still has to land under an allowed root.
    /// </summary>
    private string ResolveNewDatabaseDirectory(string folder, string? subFolder)
    {
        var roots = pathPolicy.AllowedRoots;
        if (roots.Count == 0)
        {
            throw new SqliteNewDatabaseRefusedException(NewDatabaseRules.NoRoots);
        }

        if (string.IsNullOrWhiteSpace(folder) || !roots.Contains(folder, StringComparer.Ordinal))
        {
            throw new SqliteNewDatabaseRefusedException($"Pick one of the configured folders ({string.Join(", ", roots)}).");
        }

        if (NewDatabaseRules.SubFolderError(subFolder) is { } subError)
        {
            throw new SqliteNewDatabaseRefusedException(subError);
        }

        var sub = subFolder?.Trim().Trim('/') ?? "";
        var directory = sub.Length == 0 ? folder : Path.Combine(folder, sub);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (sub.Length > 0 && !fullDirectory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SqliteNewDatabaseRefusedException("The sub-folder must stay inside the folder.");
        }

        if (!Directory.Exists(fullDirectory))
        {
            throw new SqliteNewDatabaseRefusedException(sub.Length == 0
                ? $"The folder {folder} doesn't exist on the host."
                : $"The sub-folder '{sub}' doesn't exist in {folder} — create it on the host first; SbConsole never creates directories.");
        }

        return directory;
    }

    /// <summary>The file, or any sibling, or anything at all under that name (a directory, a dangling symlink) refuses the request.</summary>
    private static void RequireNoExistingFile(string resolved, string fileName)
    {
        if (Occupied(resolved))
        {
            throw new SqliteNewDatabaseRefusedException($"{fileName} already exists in that folder — SbConsole never overwrites a file.");
        }

        foreach (var suffix in SiblingSuffixes)
        {
            if (Occupied(resolved + suffix))
            {
                throw new SqliteNewDatabaseRefusedException($"A leftover {fileName}{suffix} is in that folder — remove it on the host or pick another name.");
            }
        }
    }

    private static bool Occupied(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>O_CREAT|O_EXCL: fails rather than open a file that appeared after the check. A zero-byte file is a valid empty database.</summary>
    private static void CreateExclusively(string resolved, string fileName)
    {
        try
        {
            using var _ = new FileStream(resolved, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (Occupied(resolved))
        {
            throw new SqliteNewDatabaseRefusedException($"{fileName} already exists in that folder — SbConsole never overwrites a file.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new SqliteNewDatabaseRefusedException("SbConsole can't write to that folder — check the container user's permissions on the host.");
        }
    }

    private static void SetJournalMode(SqliteConnection c, NewDatabaseJournal journal)
    {
        var wanted = journal == NewDatabaseJournal.Wal ? "wal" : "delete";
        var actual = SqliteSql.Text(c, $"PRAGMA journal_mode = {wanted}");
        if (!string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"The file system refused journal mode {wanted.ToUpperInvariant()} (got {actual.ToUpperInvariant()}).");
        }
    }

    private static IReadOnlyList<SqliteStatement> TemplateStatements(NewDatabaseTemplate template, SqliteScript? script) => template switch
    {
        NewDatabaseTemplate.Starter => SqliteScript.Split(StarterSchema),
        NewDatabaseTemplate.Script => script!.Statements,
        _ => [],
    };

    private void DeleteDatabaseFiles(string resolved)
    {
        SqliteConnection.ClearAllPools();
        DeleteQuietly(resolved);
        foreach (var suffix in SiblingSuffixes)
        {
            DeleteQuietly(resolved + suffix);
        }
    }

    /// <summary>
    /// Spec §13's starter: users, projects, tasks -- projects → users RESTRICT, tasks → projects
    /// CASCADE, tasks → users SET NULL -- an index, a view, a dozen rows, and ANALYZE so the
    /// Overview and Tables pages have row estimates straight away. No functions in defaults: the
    /// console opens files with trusted_schema = OFF.
    /// </summary>
    internal const string StarterSchema = """
        CREATE TABLE users (
            id         INTEGER PRIMARY KEY,
            email      TEXT NOT NULL UNIQUE,
            name       TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE TABLE projects (
            id         INTEGER PRIMARY KEY,
            owner_id   INTEGER NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
            name       TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE TABLE tasks (
            id          INTEGER PRIMARY KEY,
            project_id  INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            assignee_id INTEGER REFERENCES users(id) ON DELETE SET NULL,
            title       TEXT NOT NULL,
            status      TEXT NOT NULL DEFAULT 'todo' CHECK (status IN ('todo', 'doing', 'done')),
            due_date    TEXT
        );
        CREATE INDEX idx_tasks_project_status ON tasks(project_id, status);
        CREATE VIEW v_open_tasks AS
            SELECT t.id, t.title, t.status, t.due_date, p.name AS project, u.name AS assignee
            FROM tasks t
            JOIN projects p ON p.id = t.project_id
            LEFT JOIN users u ON u.id = t.assignee_id
            WHERE t.status <> 'done';
        INSERT INTO users (id, email, name, created_at) VALUES
            (1, 'ada@example.com', 'Ada Lovelace', '2026-01-05T09:00:00Z'),
            (2, 'grace@example.com', 'Grace Hopper', '2026-01-06T10:30:00Z'),
            (3, 'alan@example.com', 'Alan Turing', '2026-01-07T14:15:00Z');
        INSERT INTO projects (id, owner_id, name, created_at) VALUES
            (1, 1, 'Analytical Engine', '2026-02-01T08:00:00Z'),
            (2, 2, 'Compiler', '2026-02-03T08:00:00Z'),
            (3, 3, 'Codebreaking', '2026-02-05T08:00:00Z');
        INSERT INTO tasks (id, project_id, assignee_id, title, status, due_date) VALUES
            (1, 1, 1, 'Draft the notes', 'done', '2026-03-01'),
            (2, 1, 3, 'Review Bernoulli example', 'doing', '2026-03-15'),
            (3, 2, 2, 'Write the A-0 loader', 'todo', '2026-04-01'),
            (4, 2, NULL, 'Find the moth', 'todo', NULL),
            (5, 3, 3, 'Build the bombe', 'doing', '2026-03-20'),
            (6, 3, 1, 'Document the method', 'todo', '2026-05-01');
        ANALYZE;
        """;
}
