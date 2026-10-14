using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Client;

public interface IPostgresOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListDatabasesAsync(string secret, CancellationToken ct = default);
    Task<PgOverview> GetOverviewAsync(string secret, CancellationToken ct = default);
    Task<int> GetBlockedCountAsync(string secret, CancellationToken ct = default);

    Task<PgSchemaTree> ListSchemaAsync(string secret, string? database, CancellationToken ct = default);
    Task<PgObjectDetail?> GetObjectDetailAsync(string secret, string? database, uint oid, CancellationToken ct = default);
    Task<string?> GetDdlAsync(string secret, string? database, uint oid, CancellationToken ct = default);
    Task<PgRowPage> PreviewRowsAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default);
    Task<CsvExport> ExportTableCsvAsync(string secret, string? database, PgRowsRequest request, CancellationToken ct = default);
    Task<string> ReindexIndexAsync(string secret, string? database, uint indexOid, CancellationToken ct = default);   // returns qualified name
    Task<string> DropIndexAsync(string secret, string? database, uint indexOid, CancellationToken ct = default);

    Task<PgQueryResult> RunQueryAsync(string secret, string? database, PgQueryRequest request, CancellationToken ct = default);
    Task<string> ExplainAsync(string secret, string? database, string sql, int timeoutS, CancellationToken ct = default);
    Task<CsvExport> ExportQueryCsvAsync(string secret, string? database, string sql, int timeoutS, CancellationToken ct = default);

    Task<PgActivity> GetActivityAsync(string secret, CancellationToken ct = default);
    Task<PgBackend?> GetBackendAsync(string secret, int pid, CancellationToken ct = default);
    Task<bool> CancelBackendAsync(string secret, int pid, CancellationToken ct = default);
    Task<bool> TerminateBackendAsync(string secret, int pid, CancellationToken ct = default);

    Task<PgMaintenance> GetMaintenanceAsync(string secret, string? database, CancellationToken ct = default);
    Task<string> RunMaintenanceAsync(string secret, string? database, uint tableOid, PgMaintenanceKind kind, CancellationToken ct = default);
}
