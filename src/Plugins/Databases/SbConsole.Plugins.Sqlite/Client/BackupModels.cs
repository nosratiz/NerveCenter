namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>A VACUUM INTO copy just written to the backup directory (design spec §11.7). FileName is the bare name inside it.</summary>
public sealed record BackupCreated(string FileName, long Bytes, TimeSpan Duration);

/// <summary>
/// What a backup and the live file are compared on (spec §11.7 detail panel): file size, user
/// tables, rows ≈ (sum of sqlite_stat1 estimates; null when the file was never analyzed) and the
/// schema fingerprint -- SHA-256 (lower hex) of the sorted `type|name|sql` lines of sqlite_schema,
/// sqlite_% objects excluded. SchemaObjects holds one `type|name|&lt;12 hex of that line's SHA-256&gt;`
/// per object, sorted, so two fingerprints can say how many objects differ.
/// </summary>
public sealed record SqliteFileFingerprint(long Bytes, long Tables, long? RowsApprox, string SchemaFingerprint, IReadOnlyList<string> SchemaObjects)
{
    /// <summary>Objects (by type and name) present in only one of the two, or whose SQL differs.</summary>
    public static int CountSchemaDifferences(SqliteFileFingerprint a, SqliteFileFingerprint b)
    {
        var left = ByObject(a.SchemaObjects);
        var right = ByObject(b.SchemaObjects);
        return left.Keys.Union(right.Keys, StringComparer.Ordinal)
            .Count(key => !left.TryGetValue(key, out var l) || !right.TryGetValue(key, out var r) || !string.Equals(l, r, StringComparison.Ordinal));
    }

    private static Dictionary<string, string> ByObject(IReadOnlyList<string> objects)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in objects)
        {
            var cut = line.LastIndexOf('|');
            result[cut < 0 ? line : line[..cut]] = cut < 0 ? "" : line[(cut + 1)..];
        }

        return result;
    }
}

/// <summary>
/// A backup opened read-only and checked: integrity_check (first MaxMessages rows; IntegrityOk
/// when the only row is "ok"), the file's SHA-256 (lower hex) and its fingerprint.
/// </summary>
public sealed record BackupVerification(bool IntegrityOk, IReadOnlyList<string> Messages, string Sha256, SqliteFileFingerprint File)
{
    public const int MaxMessages = 20;
}

/// <summary>A backup file opened for download. The caller owns (and disposes) Content.</summary>
public sealed record BackupFileStream(string FileName, Stream Content, long Length);

/// <summary>
/// A restore that ran (spec §11.7): the live file's previous content is kept as PreRestoreFileName
/// next to it; IntegrityOk / Messages / ForeignKeyViolations are the live file's integrity_check and
/// foreign_key_check right after the copy, which took CheckDuration of the whole Duration.
/// </summary>
public sealed record RestoreOutcome(
    string PreRestoreFileName,
    bool IntegrityOk,
    IReadOnlyList<string> Messages,
    int ForeignKeyViolations,
    TimeSpan CheckDuration,
    TimeSpan Duration);

/// <summary>The volume holding the live file (DriveInfo), for "~N days at this rate" (spec §11.8).</summary>
public sealed record VolumeInfo(long TotalBytes, long AvailableBytes);
