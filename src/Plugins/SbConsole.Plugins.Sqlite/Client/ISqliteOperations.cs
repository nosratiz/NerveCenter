using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Client;

/// <summary>
/// The thin wrapper every SQLite page and handler goes through (CLAUDE.md "every plugin operation
/// goes through a thin wrapper interface"), so handler/page tests substitute it directly.
/// </summary>
public interface ISqliteOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    Task<SqliteOverview> GetOverviewAsync(string secret, CancellationToken ct = default);

    Task<TableSizes> GetTableSizesAsync(string secret, CancellationToken ct = default);

    Task<IntegrityCheckOutcome> RunIntegrityCheckAsync(string secret, CancellationToken ct = default);

    Task<SchemaListing> ListSchemaAsync(string secret, CancellationToken ct = default);

    Task<TableDetail> GetTableDetailAsync(string secret, string name, CancellationToken ct = default);

    Task<IReadOnlyList<ColumnSample>> SampleColumnsAsync(string secret, string name, int sampleRows, CancellationToken ct = default);

    Task<RowPage> PreviewRowsAsync(string secret, string name, int limit, CancellationToken ct = default);

    Task<long> CountRowsAsync(string secret, string name, CancellationToken ct = default);

    Task<CsvExport> ExportCsvAsync(string secret, string name, int maxRows, long maxBytes, CancellationToken ct = default);

    // Phase 2 (design spec §11.3-§11.4). Every sql is a user script: SqliteScript.Parse refuses it
    // (SqliteScriptRefusedException) before anything opens, and it runs on an attach-limited connection.
    // backupFile (§11.7 "Open read-only"): run against that file inside BackupDir instead of the live file.

    Task<QueryResult> RunQueryAsync(string secret, string sql, int limit, string? backupFile = null, CancellationToken ct = default);

    Task<ExplainPlan> ExplainQueryAsync(string secret, string sql, string? backupFile = null, CancellationToken ct = default);

    Task<CsvExport> ExportQueryCsvAsync(string secret, string sql, int maxRows, long maxBytes, string? backupFile = null, CancellationToken ct = default);

    /// <summary>Tables and views with their columns for the Query editor's completion (§12); read-only, attach-limited, capped.</summary>
    Task<CompletionSchema> GetCompletionSchemaAsync(string secret, string? backupFile = null, CancellationToken ct = default);

    /// <summary>Writable; always rolls back. Throws SqliteReadOnlyConnectionException unless the secret's Mode is ReadWrite.</summary>
    Task<WriteScriptResult> DryRunWriteAsync(string secret, string sql, CancellationToken ct = default);

    /// <summary>Writable; one transaction, committed only if every statement succeeds.</summary>
    Task<WriteScriptResult> RunWriteAsync(string secret, string sql, CancellationToken ct = default);

    Task<WalCheckpointResult> CheckpointWalAsync(string secret, CancellationToken ct = default);

    // Backups (design spec §11.7). File names are bare names inside the secret's BackupDir; every
    // path is policy-checked. Throws SqliteBackupException when backups are disabled, the directory
    // or file is missing, or a name is refused.

    /// <summary>VACUUM INTO `&lt;base&gt;-&lt;yyyyMMdd-HHmmss&gt;.db` (then -2, -3… if taken). Never creates the directory; deletes a partial file on failure.</summary>
    Task<BackupCreated> CreateBackupAsync(string secret, DateTimeOffset takenAtUtc, CancellationToken ct = default);

    Task<BackupVerification> VerifyBackupAsync(string secret, string fileName, CancellationToken ct = default);

    /// <summary>The live file's side of the comparison: no integrity_check, no SHA-256.</summary>
    Task<SqliteFileFingerprint> GetLiveFingerprintAsync(string secret, CancellationToken ct = default);

    /// <summary>Deletes the file (and any -wal/-shm/-journal sibling) inside BackupDir; a missing file is success.</summary>
    Task DeleteBackupFileAsync(string secret, string fileName, CancellationToken ct = default);

    Task<BackupFileStream> OpenBackupStreamAsync(string secret, string fileName, CancellationToken ct = default);

    /// <summary>Needs Mode=ReadWrite (SqliteReadOnlyConnectionException). Aborts before touching the live file unless the backup verifies.</summary>
    Task<RestoreOutcome> RestoreBackupAsync(string secret, string fileName, DateTimeOffset nowUtc, CancellationToken ct = default);

    // Growth (design spec §11.8).

    /// <summary>Total/available bytes of the live file's volume; null when unavailable.</summary>
    Task<VolumeInfo?> GetVolumeAsync(string secret, CancellationToken ct = default);

    /// <summary>The live file's size (main file only); null when it doesn't exist.</summary>
    Task<long?> GetFileBytesAsync(string secret, CancellationToken ct = default);

    // Tables page DDL (design spec §11.5). All need Mode=ReadWrite (SqliteReadOnlyConnectionException);
    // names must be user tables in sqlite_schema (SqliteDdlRefusedException otherwise).

    /// <summary>Exactly one CREATE TABLE statement, in a transaction on an attach-limited writable connection; returns the new table's name.</summary>
    Task<string> CreateTableAsync(string secret, string ddl, CancellationToken ct = default);

    Task RenameTableAsync(string secret, string oldName, string newName, CancellationToken ct = default);

    /// <summary>Read-only: what the Drop dialog shows.</summary>
    Task<DropImpact> GetDropImpactAsync(string secret, string name, CancellationToken ct = default);

    /// <summary>DROP TABLE in a transaction; a RESTRICT / NO ACTION reference with rows makes it fail and roll back.</summary>
    Task DropTableAsync(string secret, string name, CancellationToken ct = default);

    // Corrupt state (design spec §11.6).

    /// <summary>REINDEX, then a fresh integrity_check + foreign_key_check.</summary>
    Task<IntegrityCheckOutcome> ReindexAsync(string secret, CancellationToken ct = default);

    /// <summary>
    /// Best-effort copy of every table, row by row, then indexes/views/triggers, into a new
    /// `&lt;base&gt;-recovered-&lt;yyyyMMdd-HHmmss&gt;.db` inside BackupDir. The live file is only read.
    /// </summary>
    Task<SalvageOutcome> SalvageAsync(string secret, DateTimeOffset nowUtc, CancellationToken ct = default);
}
