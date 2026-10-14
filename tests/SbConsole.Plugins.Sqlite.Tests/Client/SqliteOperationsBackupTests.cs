using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Backups against real temp files (design spec §11.7): VACUUM INTO, verify, delete, download, restore.</summary>
public sealed class SqliteOperationsBackupTests : IDisposable
{
    private static readonly DateTimeOffset TakenAt = new(2026, 10, 13, 2, 0, 5, TimeSpan.Zero);

    private readonly TempDatabase _db = new("""
        CREATE TABLE events(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
        CREATE INDEX idx_events_name ON events(name);
        INSERT INTO events(name) VALUES ('a'), ('b'), ('c');
        """, fileName: "reports.db");

    private string BackupSecret(SqliteAccessMode mode = SqliteAccessMode.ReadOnly) => _db.Secret(mode: mode, backupDir: _db.BackupDir);

    public void Dispose() => _db.Dispose();

    private static long Count(string path, string sql = "SELECT COUNT(*) FROM events")
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task A_backup_is_a_vacuumed_copy_named_after_the_file_and_the_time()
    {
        var created = await _db.Operations().CreateBackupAsync(BackupSecret(), TakenAt);

        created.FileName.Should().Be("reports-20261013-020005.db");
        var path = Path.Combine(_db.BackupDir, created.FileName);
        created.Bytes.Should().Be(new FileInfo(path).Length).And.BePositive();
        created.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        Count(path).Should().Be(3);
    }

    [Fact]
    public async Task A_name_already_taken_gets_a_numeric_suffix()
    {
        var operations = _db.Operations();

        var first = await operations.CreateBackupAsync(BackupSecret(), TakenAt);
        var second = await operations.CreateBackupAsync(BackupSecret(), TakenAt);
        var third = await operations.CreateBackupAsync(BackupSecret(), TakenAt);

        new[] { first.FileName, second.FileName, third.FileName }.Should().Equal(
            "reports-20261013-020005.db", "reports-20261013-020005-2.db", "reports-20261013-020005-3.db");
    }

    [Fact]
    public async Task A_missing_backup_directory_is_refused_and_never_created()
    {
        var missing = Path.Combine(_db.Directory, "nowhere");

        var act = () => _db.Operations().CreateBackupAsync(_db.Secret(backupDir: missing), TakenAt);

        (await act.Should().ThrowAsync<SqliteBackupException>()).Which.Message.Should().StartWith("Backup directory does not exist");
        Directory.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_backup_directory_backups_are_not_enabled()
    {
        var act = () => _db.Operations().CreateBackupAsync(_db.Secret(), TakenAt);

        (await act.Should().ThrowAsync<SqliteBackupException>()).Which.Message.Should().Be(SqliteOperations.BackupsNotEnabled);
    }

    [Fact]
    public async Task The_backup_directory_must_pass_the_path_policy()
    {
        var dir = _db.BackupDir;
        var policy = Substitute.For<IHostPathPolicy>();
        policy.Check(Arg.Any<string>()).Returns(call => call.Arg<string>().StartsWith(dir, StringComparison.Ordinal)
            ? PathCheckResult.Refuse("outside the roots")
            : PathCheckResult.Allow(call.Arg<string>()));

        var act = () => new SqliteOperations(policy).CreateBackupAsync(BackupSecret(), TakenAt);

        await act.Should().ThrowAsync<SqlitePathRefusedException>().WithMessage("outside the roots");
        Directory.GetFiles(dir).Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_backup_leaves_no_file()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _db.Operations().CreateBackupAsync(BackupSecret(), TakenAt, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(_db.BackupDir).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_vacuum_into_deletes_its_partial_file()
    {
        // A busy writer holding an EXCLUSIVE lock makes the read-only VACUUM INTO fail once its busy timeout runs out.
        using var writer = _db.OpenWriter();
        TempDatabase.Run(writer, "PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE; INSERT INTO events(name) VALUES ('d');");

        var act = () => _db.Operations().CreateBackupAsync(_db.Secret(busyTimeoutMs: 100, backupDir: _db.BackupDir), TakenAt);

        await act.Should().ThrowAsync<SqliteException>();
        Directory.GetFiles(_db.BackupDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Verify_reports_integrity_hash_tables_rows_and_fingerprint()
    {
        _db.Execute("ANALYZE");
        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);

        var verification = await operations.VerifyBackupAsync(BackupSecret(), created.FileName);

        var path = Path.Combine(_db.BackupDir, created.FileName);
        verification.IntegrityOk.Should().BeTrue();
        verification.Messages.Should().Equal("ok");
        verification.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        verification.File.Bytes.Should().Be(created.Bytes);
        verification.File.Tables.Should().Be(1);
        verification.File.RowsApprox.Should().Be(3);
        verification.File.SchemaFingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        verification.File.SchemaObjects.Should().HaveCount(2).And.Contain(o => o.StartsWith("index|idx_events_name|", StringComparison.Ordinal));

        var live = await operations.GetLiveFingerprintAsync(BackupSecret());
        live.SchemaFingerprint.Should().Be(verification.File.SchemaFingerprint);
        live.RowsApprox.Should().Be(3);
        live.Tables.Should().Be(1);
        SqliteFileFingerprint.CountSchemaDifferences(live, verification.File).Should().Be(0);
    }

    [Fact]
    public async Task Rows_are_unknown_without_statistics()
    {
        (await _db.Operations().GetLiveFingerprintAsync(BackupSecret())).RowsApprox.Should().BeNull();
    }

    [Fact]
    public async Task The_live_fingerprint_counts_objects_that_differ_from_a_backup()
    {
        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);
        _db.Execute("CREATE TABLE extra(x); DROP INDEX idx_events_name; CREATE INDEX idx_events_name ON events(name, id);");

        var backup = (await operations.VerifyBackupAsync(BackupSecret(), created.FileName)).File;
        var live = await operations.GetLiveFingerprintAsync(BackupSecret());

        live.SchemaFingerprint.Should().NotBe(backup.SchemaFingerprint);
        live.Tables.Should().Be(2);
        SqliteFileFingerprint.CountSchemaDifferences(live, backup).Should().Be(2);
    }

    [Fact]
    public async Task Verifying_a_file_that_is_not_a_database_fails()
    {
        await File.WriteAllTextAsync(Path.Combine(_db.BackupDir, "junk.db"), new string('x', 8192));

        var act = () => _db.Operations().VerifyBackupAsync(BackupSecret(), "junk.db");

        (await act.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(26);
    }

    [Theory]
    [InlineData("../reports.db")]
    [InlineData("sub/x.db")]
    [InlineData("..")]
    [InlineData("")]
    public async Task Names_with_separators_or_dot_dot_are_refused(string fileName)
    {
        var operations = _db.Operations();

        var verify = () => operations.VerifyBackupAsync(BackupSecret(), fileName);
        var delete = () => operations.DeleteBackupFileAsync(BackupSecret(), fileName);
        var open = () => operations.OpenBackupStreamAsync(BackupSecret(), fileName);
        var query = () => operations.RunQueryAsync(BackupSecret(), "SELECT 1", 10, fileName);

        await verify.Should().ThrowAsync<SqliteBackupException>();
        await delete.Should().ThrowAsync<SqliteBackupException>();
        await open.Should().ThrowAsync<SqliteBackupException>();
        await query.Should().ThrowAsync<SqliteBackupException>();
        File.Exists(_db.Path).Should().BeTrue();
    }

    [Fact]
    public async Task A_symlink_leading_out_of_the_backup_directory_is_refused()
    {
        var link = Path.Combine(_db.BackupDir, "escape.db");
        File.CreateSymbolicLink(link, _db.Path);
        var policy = Substitute.For<IHostPathPolicy>();
        policy.Check(Arg.Any<string>()).Returns(call =>
        {
            var path = call.Arg<string>();
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return PathCheckResult.Allow(target?.FullName ?? path);
        });

        var act = () => new SqliteOperations(policy).DeleteBackupFileAsync(BackupSecret(), "escape.db");

        (await act.Should().ThrowAsync<SqliteBackupException>()).Which.Message.Should().Contain("resolves outside");
        File.Exists(_db.Path).Should().BeTrue();
    }

    [Fact]
    public async Task Verifying_a_missing_backup_says_so()
    {
        var act = () => _db.Operations().VerifyBackupAsync(BackupSecret(), "gone.db");

        (await act.Should().ThrowAsync<SqliteBackupException>()).Which.Message.Should().Be("Backup file 'gone.db' was not found in the backup directory.");
    }

    [Fact]
    public async Task Delete_removes_the_file_and_a_missing_file_or_directory_is_success()
    {
        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);

        await operations.DeleteBackupFileAsync(BackupSecret(), created.FileName);
        await operations.DeleteBackupFileAsync(BackupSecret(), created.FileName);
        await operations.DeleteBackupFileAsync(_db.Secret(backupDir: Path.Combine(_db.Directory, "nowhere")), created.FileName);

        File.Exists(Path.Combine(_db.BackupDir, created.FileName)).Should().BeFalse();
        File.Exists(_db.Path).Should().BeTrue();
    }

    [Fact]
    public async Task A_download_streams_the_file_read_only()
    {
        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);

        var download = await operations.OpenBackupStreamAsync(BackupSecret(), created.FileName);
        await using (download.Content)
        {
            download.FileName.Should().Be(created.FileName);
            download.Length.Should().Be(created.Bytes);
            download.Content.CanWrite.Should().BeFalse();
            using var copy = new MemoryStream();
            await download.Content.CopyToAsync(copy);
            copy.ToArray().Should().Equal(await File.ReadAllBytesAsync(Path.Combine(_db.BackupDir, created.FileName)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_puts_the_backup_back_and_keeps_the_old_content_beside_the_file(bool wal)
    {
        if (wal)
        {
            _db.Execute("PRAGMA journal_mode=WAL");
        }

        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);
        _db.Execute("DELETE FROM events WHERE name = 'a'; INSERT INTO events(name) VALUES ('later');");

        var outcome = await operations.RestoreBackupAsync(BackupSecret(SqliteAccessMode.ReadWrite), created.FileName,
            new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero));

        outcome.PreRestoreFileName.Should().Be("reports.db.pre-restore-20261013-093000");
        outcome.IntegrityOk.Should().BeTrue();
        outcome.Messages.Should().Equal("ok");
        outcome.ForeignKeyViolations.Should().Be(0);
        outcome.CheckDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        SqliteConnection.ClearAllPools();
        Count(_db.Path, "SELECT COUNT(*) FROM events WHERE name IN ('a', 'b', 'c')").Should().Be(3);
        Count(_db.Path, "SELECT COUNT(*) FROM events WHERE name = 'later'").Should().Be(0);
        var preRestore = Path.Combine(_db.Directory, outcome.PreRestoreFileName);
        Count(preRestore, "SELECT COUNT(*) FROM events WHERE name = 'later'").Should().Be(1);
        Count(preRestore, "SELECT COUNT(*) FROM events WHERE name = 'a'").Should().Be(0);
    }

    [Fact]
    public async Task Restore_counts_foreign_key_violations_in_the_restored_file()
    {
        _db.Execute("""
            PRAGMA foreign_keys = OFF;
            CREATE TABLE parent(id INTEGER PRIMARY KEY);
            CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
            INSERT INTO child VALUES (1, 7), (2, 8);
            """);
        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);

        var outcome = await operations.RestoreBackupAsync(BackupSecret(SqliteAccessMode.ReadWrite), created.FileName, TakenAt);

        outcome.IntegrityOk.Should().BeTrue();
        outcome.ForeignKeyViolations.Should().Be(2);
    }

    [Fact]
    public async Task Restore_refuses_a_read_only_connection_before_anything_is_copied()
    {
        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);

        var act = () => operations.RestoreBackupAsync(BackupSecret(), created.FileName, TakenAt);

        await act.Should().ThrowAsync<SqliteReadOnlyConnectionException>();
        Directory.GetFiles(_db.Directory, "*.pre-restore-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_of_an_unreadable_backup_aborts_and_leaves_the_live_file_untouched()
    {
        await File.WriteAllTextAsync(Path.Combine(_db.BackupDir, "junk.db"), new string('x', 8192));
        var before = await File.ReadAllBytesAsync(_db.Path);

        var act = () => _db.Operations().RestoreBackupAsync(BackupSecret(SqliteAccessMode.ReadWrite), "junk.db", TakenAt);

        await act.Should().ThrowAsync<SqliteException>();
        (await File.ReadAllBytesAsync(_db.Path)).Should().Equal(before);
        Directory.GetFiles(_db.Directory, "*.pre-restore-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_of_a_corrupt_backup_aborts_and_leaves_the_live_file_untouched()
    {
        var operations = _db.Operations();
        _db.Execute("WITH RECURSIVE s(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM s WHERE v < 2000) "
            + "INSERT INTO events(name) SELECT printf('row-%d-%s', v, hex(randomblob(40))) FROM s");
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);
        var path = Path.Combine(_db.BackupDir, created.FileName);
        var bytes = await File.ReadAllBytesAsync(path);
        // Trash every page but the first (header + sqlite_schema): the schema reads, the b-trees don't.
        for (var i = 4096; i < bytes.Length; i++)
        {
            bytes[i] = 0x5A;
        }

        await File.WriteAllBytesAsync(path, bytes);
        var before = await File.ReadAllBytesAsync(_db.Path);

        var act = () => operations.RestoreBackupAsync(BackupSecret(SqliteAccessMode.ReadWrite), created.FileName, TakenAt);

        // Either integrity_check reports problems (SqliteBackupException) or reading the page fails (SQLITE_CORRUPT).
        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().Match<Exception>(e => e is SqliteBackupException || e is SqliteException);
        FriendlySqliteError.From(thrown.Which).Should().BeOneOf(
            "The backup failed its integrity check — nothing was restored.", "The file is not a valid or healthy SQLite database.");
        (await File.ReadAllBytesAsync(_db.Path)).Should().Equal(before);
        Directory.GetFiles(_db.Directory, "*.pre-restore-*").Should().BeEmpty();
    }

    [Fact]
    public async Task The_volume_of_the_live_file_is_reported()
    {
        var volume = await _db.Operations().GetVolumeAsync(_db.Secret());

        volume.Should().NotBeNull();
        volume!.TotalBytes.Should().BePositive();
        volume.AvailableBytes.Should().BeInRange(0, volume.TotalBytes);
    }

    [Fact]
    public async Task File_bytes_are_the_main_file_size_or_null_when_missing()
    {
        (await _db.Operations().GetFileBytesAsync(_db.Secret())).Should().Be(new FileInfo(_db.Path).Length);
        (await _db.Operations().GetFileBytesAsync(new SqliteConnectionSettings(Path.Combine(_db.Directory, "none.db")).ToSecretString())).Should().BeNull();
    }

    [Fact]
    public async Task Queries_can_run_against_a_backup_instead_of_the_live_file()
    {
        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);
        _db.Execute("DELETE FROM events");

        var live = await operations.RunQueryAsync(BackupSecret(), "SELECT COUNT(*) FROM events", 10);
        var backup = await operations.RunQueryAsync(BackupSecret(), "SELECT COUNT(*) FROM events", 10, created.FileName);
        var plan = await operations.ExplainQueryAsync(BackupSecret(), "SELECT * FROM events WHERE name = 'a'", created.FileName);
        var export = await operations.ExportQueryCsvAsync(BackupSecret(), "SELECT name FROM events ORDER BY name", 100, CsvExport.MaxBytes, created.FileName);

        live.Rows[0][0].Integer.Should().Be(0);
        backup.Rows[0][0].Integer.Should().Be(3);
        plan.Nodes.Should().Contain(n => n.Detail.Contains("idx_events_name", StringComparison.Ordinal));
        export.RowCount.Should().Be(3);
    }

    [Fact]
    public async Task A_backup_query_is_still_read_only_and_attach_limited()
    {
        var operations = _db.Operations();
        var created = await operations.CreateBackupAsync(BackupSecret(), TakenAt);

        var write = () => operations.RunQueryAsync(BackupSecret(SqliteAccessMode.ReadWrite), "DELETE FROM events", 10, created.FileName);
        var attach = () => operations.RunQueryAsync(BackupSecret(), "SELECT * FROM pragma_database_list; ATTACH 'x.db' AS x", 10, created.FileName);
        var missing = () => operations.RunQueryAsync(BackupSecret(), "SELECT 1", 10, "gone.db");

        (await write.Should().ThrowAsync<SqliteException>()).Which.SqliteErrorCode.Should().Be(8);
        await attach.Should().ThrowAsync<SqliteScriptRefusedException>();
        await missing.Should().ThrowAsync<SqliteBackupException>();
        Count(Path.Combine(_db.BackupDir, created.FileName)).Should().Be(3);
    }
}
