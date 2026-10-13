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

    Task<QueryResult> RunQueryAsync(string secret, string sql, int limit, CancellationToken ct = default);

    Task<ExplainPlan> ExplainQueryAsync(string secret, string sql, CancellationToken ct = default);

    Task<CsvExport> ExportQueryCsvAsync(string secret, string sql, int maxRows, long maxBytes, CancellationToken ct = default);

    /// <summary>Writable; always rolls back. Throws SqliteReadOnlyConnectionException unless the secret's Mode is ReadWrite.</summary>
    Task<WriteScriptResult> DryRunWriteAsync(string secret, string sql, CancellationToken ct = default);

    /// <summary>Writable; one transaction, committed only if every statement succeeds.</summary>
    Task<WriteScriptResult> RunWriteAsync(string secret, string sql, CancellationToken ct = default);

    Task<WalCheckpointResult> CheckpointWalAsync(string secret, CancellationToken ct = default);
}
