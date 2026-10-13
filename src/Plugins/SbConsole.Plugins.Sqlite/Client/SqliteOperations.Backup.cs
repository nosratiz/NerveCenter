using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// Backups (mockup 1e, design spec §11.7). Every path -- the live file, the backup directory, each
/// backup file and the pre-restore copy -- goes through IHostPathPolicy, and only the resolved path
/// is used. Backup files are bare names inside BackupDir; anything with a separator or ".." is
/// refused, and a name that resolves (through a symlink) outside BackupDir is refused too.
/// Directories are never created.
/// </summary>
public sealed partial class SqliteOperations
{
    public const string BackupsNotEnabled = "Backups are not enabled for this connection — set \"Enable backups to\" in Connections.";

    private const string StampFormat = "yyyyMMdd-HHmmss";

    public Task<BackupCreated> CreateBackupAsync(string secret, DateTimeOffset takenAtUtc, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var settings = SqliteConnectionSettings.Parse(secret);
            var live = ResolveLivePath(settings);
            var dir = ResolveBackupDir(settings, mustExist: true)!;
            var name = FreeName(dir, $"{Path.GetFileNameWithoutExtension(live)}-{Stamp(takenAtUtc)}", ".db");
            var target = ResolveInside(dir, name);
            var started = Stopwatch.GetTimestamp();
            VacuumInto(secret, target, ct);
            return new BackupCreated(name, new FileInfo(target).Length, Stopwatch.GetElapsedTime(started));
        }, ct);

    public Task<BackupVerification> VerifyBackupAsync(string secret, string fileName, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var (messages, fingerprint, path) = Run(secret, SqliteOpenOptions.BackupRead(fileName),
                c => (IntegrityMessages(c), Fingerprint(c), c.DataSource), ct);
            return new BackupVerification(IsOk(messages), messages, Sha256Of(path, ct), fingerprint);
        }, ct);

    public Task<SqliteFileFingerprint> GetLiveFingerprintAsync(string secret, CancellationToken ct = default) =>
        RunAsync(secret, Fingerprint, ct);

    public Task DeleteBackupFileAsync(string secret, string fileName, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var settings = SqliteConnectionSettings.Parse(secret);
            ValidateFileName(fileName);
            var dir = ResolveBackupDir(settings, mustExist: false);
            if (dir is null)
            {
                return; // No directory = no file: already gone.
            }

            var path = ResolveInside(dir, fileName);
            // Siblings a read-only open of a WAL-mode copy may have left behind.
            foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            {
                File.Delete(file); // Missing file: no-op.
            }
        }, ct);

    public Task<BackupFileStream> OpenBackupStreamAsync(string secret, string fileName, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var path = RequireBackupFile(SqliteConnectionSettings.Parse(secret), fileName);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new BackupFileStream(fileName, stream, stream.Length);
        }, ct);

    /// <summary>
    /// Verify the backup (abort unless ok) → VACUUM INTO `&lt;live&gt;.pre-restore-&lt;stamp&gt;` next to
    /// the live file → online backup API from the backup (read-only) into a writable connection on
    /// the live file → integrity_check of the live file. Nothing touches the live file before the
    /// backup has verified and the pre-restore copy exists.
    /// </summary>
    public Task<RestoreOutcome> RestoreBackupAsync(string secret, string fileName, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var started = Stopwatch.GetTimestamp();
            var settings = SqliteConnectionSettings.Parse(secret);
            // Re-read from the decrypted secret before anything is copied (§11.1).
            if (settings.Mode != SqliteAccessMode.ReadWrite)
            {
                throw new SqliteReadOnlyConnectionException();
            }

            var backupPath = RequireBackupFile(settings, fileName);
            var check = Run(secret, SqliteOpenOptions.BackupRead(fileName), IntegrityMessages, ct);
            if (!IsOk(check))
            {
                throw new SqliteBackupException("The backup failed its integrity check — nothing was restored.");
            }

            var live = ResolveLivePath(settings);
            var liveDir = Path.GetDirectoryName(live)!;
            var preName = FreeName(liveDir, $"{Path.GetFileName(live)}.pre-restore-{Stamp(nowUtc)}", "");
            VacuumInto(secret, ResolveInside(liveDir, preName), ct);

            var after = Run(secret, new SqliteOpenOptions(Writable: true, Timeout: SqliteOpenOptions.BackupTimeout), c =>
            {
                using var source = Open(backupPath, settings, SqliteOpenOptions.Plugin);
                source.BackupDatabase(c);
                return IntegrityMessages(c);
            }, ct);

            _logger.LogInformation("Restored {Backup} over {Live}; previous content kept as {PreRestore}.", backupPath, live, preName);
            return new RestoreOutcome(preName, IsOk(after), after, Stopwatch.GetElapsedTime(started));
        }, ct);

    public Task<VolumeInfo?> GetVolumeAsync(string secret, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var live = ResolveLivePath(SqliteConnectionSettings.Parse(secret));
            try
            {
                // On Unix DriveInfo statvfs()es whatever path it is given, so the file's own directory
                // names its volume; on Windows it needs the root.
                var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(live)! : Path.GetDirectoryName(live)!);
                return drive.TotalSize > 0 ? new VolumeInfo(drive.TotalSize, drive.AvailableFreeSpace) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogDebug(ex, "Reading the volume of {Path} failed.", live);
                return null;
            }
        }, ct);

    public Task<long?> GetFileBytesAsync(string secret, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(ResolveLivePath(SqliteConnectionSettings.Parse(secret)));
            return file.Exists ? file.Length : (long?)null;
        }, ct);

    private string ResolveLivePath(SqliteConnectionSettings settings)
    {
        var path = pathPolicy.Check(settings.Path);
        return path.Allowed ? path.ResolvedPath! : throw new SqlitePathRefusedException(path.Reason ?? "Path refused.");
    }

    /// <summary>The policy-resolved BackupDir; null when it doesn't exist and <paramref name="mustExist"/> is false.</summary>
    private string? ResolveBackupDir(SqliteConnectionSettings settings, bool mustExist)
    {
        if (settings.BackupDir is null)
        {
            throw new SqliteBackupException(BackupsNotEnabled);
        }

        var check = pathPolicy.Check(settings.BackupDir);
        if (!check.Allowed)
        {
            throw new SqlitePathRefusedException(check.Reason ?? "Path refused.");
        }

        var dir = Path.TrimEndingDirectorySeparator(check.ResolvedPath!);
        if (Directory.Exists(dir))
        {
            return dir;
        }

        return mustExist ? throw new SqliteBackupException($"Backup directory does not exist: {settings.BackupDir}") : null;
    }

    private string RequireBackupFile(SqliteConnectionSettings settings, string fileName)
    {
        ValidateFileName(fileName);
        var path = ResolveInside(ResolveBackupDir(settings, mustExist: true)!, fileName);
        return File.Exists(path) ? path : throw new SqliteBackupException($"Backup file '{fileName}' was not found in the backup directory.");
    }

    /// <summary><paramref name="fileName"/> inside <paramref name="dir"/> (already resolved), re-checked by the policy and required to stay there.</summary>
    private string ResolveInside(string dir, string fileName)
    {
        var check = pathPolicy.Check(Path.Combine(dir, fileName));
        if (!check.Allowed)
        {
            throw new SqlitePathRefusedException(check.Reason ?? "Path refused.");
        }

        var resolved = check.ResolvedPath!;
        return string.Equals(Path.GetDirectoryName(resolved), dir, StringComparison.Ordinal)
            ? resolved
            : throw new SqliteBackupException($"'{fileName}' resolves outside its directory.");
    }

    internal static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.IndexOfAny(['/', '\\', '\0']) >= 0 || fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new SqliteBackupException($"'{fileName}' is not a valid backup file name.");
        }
    }

    /// <summary>`stem + ext`, or `stem-2 + ext`, `stem-3 + ext`… -- the first that doesn't exist yet in <paramref name="dir"/>.</summary>
    private static string FreeName(string dir, string stem, string extension)
    {
        var name = stem + extension;
        for (var n = 2; Path.Exists(Path.Combine(dir, name)); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}{extension}");
        }

        return name;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);

    /// <summary>VACUUM INTO on a read-only, not attach-limited, plugin-owned connection; a partial file is deleted on any failure.</summary>
    private void VacuumInto(string secret, string target, CancellationToken ct)
    {
        try
        {
            Run(secret, SqliteOpenOptions.Backup, c =>
            {
                SqliteSql.Execute(c, "VACUUM INTO @path", ("@path", target));
                return true;
            }, ct);
        }
        catch
        {
            DeleteQuietly(target);
            throw;
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Deleting the partial file {Path} failed.", path);
        }
    }

    private static List<string> IntegrityMessages(SqliteConnection c) =>
        SqliteSql.Rows(c, $"PRAGMA integrity_check({BackupVerification.MaxMessages.ToString(CultureInfo.InvariantCulture)})", r => r.GetString(0));

    private static bool IsOk(IReadOnlyList<string> messages) => messages is ["ok"];

    internal static SqliteFileFingerprint Fingerprint(SqliteConnection c)
    {
        var objects = SqliteSql.Rows(c, @"SELECT type, name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite\_%' ESCAPE '\'",
            r => (Type: r.GetString(0), Name: r.GetString(1), Sql: r.IsDBNull(2) ? "" : r.GetString(2)));
        var lines = objects.Select(o => $"{o.Type}|{o.Name}|{o.Sql}").Order(StringComparer.Ordinal).ToList();
        var perObject = objects
            .Select(o => $"{o.Type}|{o.Name}|{Sha256Hex($"{o.Type}|{o.Name}|{o.Sql}")[..12]}")
            .Order(StringComparer.Ordinal)
            .ToList();

        var tables = objects.Where(o => o.Type == "table").Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var stat1 = ReadStat1Rows(c).Where(kv => tables.Contains(kv.Key)).ToList();
        return new SqliteFileFingerprint(
            new FileInfo(c.DataSource).Length,
            tables.Count,
            stat1.Count == 0 ? null : stat1.Sum(kv => kv.Value),
            Sha256Hex(string.Join("\n", lines)),
            perObject);
    }

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Sha256Of(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81_920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
